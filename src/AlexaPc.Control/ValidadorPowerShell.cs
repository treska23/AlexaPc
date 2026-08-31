using System.IO;
using System.Management.Automation.Language;
using System.Text.RegularExpressions;

namespace ControlPCIA;

internal sealed record ResultadoValidacionPowerShell(
    bool Permitido,
    string Motivo);

/// <summary>
/// Política de denegación mínima de ControlPCIA.
///
/// Sólo bloquea:
/// 1. eliminar elementos o contenido;
/// 2. mover o cortar elementos;
/// 3. formatear, limpiar o reinicializar discos y unidades.
///
/// La interfaz gráfica antigua también se rechaza porque fue retirada del
/// producto: ControlPCIA ejecuta comandos, CLI, API y protocolos de consola.
/// </summary>
internal static class ValidadorPowerShell
{
    private const int LongitudMaxima = 20_000;

    private static readonly Dictionary<string, string> Alias =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["rm"] = "Remove-Item",
            ["ri"] = "Remove-Item",
            ["del"] = "Remove-Item",
            ["erase"] = "Remove-Item",
            ["rd"] = "Remove-Item",
            ["rmdir"] = "Remove-Item",
            ["clc"] = "Clear-Content",
            ["cli"] = "Clear-Item",
            ["mv"] = "Move-Item",
            ["move"] = "Move-Item",
            ["mi"] = "Move-Item",
            ["ren"] = "Rename-Item",
            ["rni"] = "Rename-Item"
        };

    public static ResultadoValidacionPowerShell Validar(
        string comando,
        bool permitirDescarte = false)
    {
        _ = permitirDescarte;

        if (string.IsNullOrWhiteSpace(comando))
        {
            return Bloquear("El comando está vacío.");
        }

        comando = comando.Trim();

        if (comando.Length > LongitudMaxima)
        {
            return Bloquear("El comando supera la longitud máxima permitida.");
        }

        ScriptBlockAst ast = Parser.ParseInput(
            comando,
            out Token[] tokens,
            out ParseError[] errores);

        if (errores.Length > 0
            || tokens.Any(token => token.Kind == TokenKind.Unknown))
        {
            string detalle = errores.FirstOrDefault()?.Message
                ?? "se encontró un elemento no reconocido";
            return Bloquear(
                $"La sintaxis de PowerShell no es válida: {detalle}");
        }

        ResultadoValidacionPowerShell? bloqueo =
            ValidarArquitecturaConsola(comando);
        bloqueo ??= ValidarEvasionesNoInspeccionables(comando);

        if (bloqueo is not null)
        {
            return bloqueo;
        }

        foreach (CommandAst comandoAst in ast
                     .FindAll(
                         nodo => nodo is CommandAst,
                         searchNestedScriptBlocks: true)
                     .Cast<CommandAst>())
        {
            bloqueo = ValidarComando(comandoAst);

            if (bloqueo is not null)
            {
                return bloqueo;
            }
        }

        foreach (InvokeMemberExpressionAst invocacion in ast
                     .FindAll(
                         nodo => nodo is InvokeMemberExpressionAst,
                         searchNestedScriptBlocks: true)
                     .Cast<InvokeMemberExpressionAst>())
        {
            bloqueo = ValidarMetodo(invocacion);

            if (bloqueo is not null)
            {
                return bloqueo;
            }
        }

        return new(
            true,
            "Permitido: no elimina, no mueve/corta y no formatea unidades.");
    }

    private static ResultadoValidacionPowerShell?
        ValidarArquitecturaConsola(string comando)
    {
        return Regex.IsMatch(
            comando,
            @"\bControlPCIA(?:\.exe)?\s+(?:ui|window\s+keys)\b|\.SendKeys\s*\(|\bSystem\.Windows\.Forms\.SendKeys\b|\.SendWait\s*\(|\bUIAutomation(?:Client)?\b",
            RegexOptions.IgnoreCase
            | RegexOptions.CultureInvariant)
                ? Bloquear(
                    "La automatización gráfica está retirada: ControlPCIA sólo utiliza interfaces invocables desde consola.")
                : null;
    }

    private static ResultadoValidacionPowerShell?
        ValidarEvasionesNoInspeccionables(string comando)
    {
        if (Regex.IsMatch(
                comando,
                @"(?:^|\s)-(?:EncodedCommand|EncodedArguments)\b|\b(?:Invoke-Expression|iex)\b",
                RegexOptions.IgnoreCase
                | RegexOptions.CultureInvariant))
        {
            return Bloquear(
                "No se admite código oculto o construido dinámicamente porque impediría comprobar las tres prohibiciones.");
        }

        return null;
    }

    private static ResultadoValidacionPowerShell? ValidarComando(
        CommandAst comando)
    {
        string? nombreOriginal = comando.GetCommandName();

        if (string.IsNullOrWhiteSpace(nombreOriginal))
        {
            return Bloquear(
                    "El nombre dinámico del comando no permite comprobar que no elimine, mueva ni formatee.");
        }

        string nombre = NormalizarAlias(nombreOriginal);

        if (EsEliminacion(nombre))
        {
            return Bloquear(
                $"El comando '{nombreOriginal}' elimina elementos o contenido.");
        }

        if (EsMovimiento(nombre))
        {
            return Bloquear(
                $"El comando '{nombreOriginal}' mueve o corta elementos.");
        }

        if (EsFormatoDeUnidad(nombre))
        {
            return Bloquear(
                $"El comando '{nombreOriginal}' puede formatear o reinicializar una unidad.");
        }

        string texto = comando.Extent.Text;

        if (ContieneOperacionProhibidaEnInterprete(nombre, texto))
        {
            return Bloquear(
                "El intérprete contiene una operación de eliminación, movimiento o formato.");
        }

        if (ContieneOperacionProhibidaEnUtilidad(nombre, texto))
        {
            return Bloquear(
                "La utilidad solicita eliminar, mover o formatear.");
        }

        if ((nombre.Equals(
                 "Invoke-CimMethod",
                 StringComparison.OrdinalIgnoreCase)
             || nombre.Equals(
                 "Invoke-WmiMethod",
                 StringComparison.OrdinalIgnoreCase)
             || NombreSimple(nombre).Equals(
                 "wmic",
                 StringComparison.OrdinalIgnoreCase))
            && Regex.IsMatch(
                texto,
                @"(?:^|\s)-(?:MethodName|Name)\s+['""]?(?:Delete|Remove|Move|Format)\b|\bcall\s+(?:Delete|Remove|Move|Format)\b",
                RegexOptions.IgnoreCase
                | RegexOptions.CultureInvariant))
        {
            return Bloquear(
                "La llamada WMI/CIM solicita eliminar, mover o formatear.");
        }

        if (nombre.Equals(
                "Start-Process",
                StringComparison.OrdinalIgnoreCase)
            && ContieneLanzamientoAnidadoProhibido(texto))
        {
            return Bloquear(
                "El proceso anidado contiene una operación de eliminación, movimiento o formato.");
        }

        return null;
    }

    private static ResultadoValidacionPowerShell? ValidarMetodo(
        InvokeMemberExpressionAst invocacion)
    {
        string? miembro = ObtenerNombreMiembro(invocacion.Member);

        if (miembro is null)
        {
            return Bloquear(
                "El nombre dinámico del método no permite comprobar las tres prohibiciones.");
        }

        if (EsMetodoEliminacion(miembro))
        {
            return Bloquear(
                $"El método '{miembro}' elimina elementos o contenido.");
        }

        if (EsMetodoMovimiento(invocacion, miembro))
        {
            return Bloquear(
                $"El método '{miembro}' mueve o corta elementos.");
        }

        if (miembro.Equals(
                "Format",
                StringComparison.OrdinalIgnoreCase)
            && Regex.IsMatch(
                invocacion.Extent.Text,
                @"\b(?:Disk|Drive|Partition|Volume|FileSystem|Win32_)\b",
                RegexOptions.IgnoreCase
                | RegexOptions.CultureInvariant))
        {
            return Bloquear("No se permite formatear una unidad.");
        }

        return null;
    }

    private static bool EsEliminacion(string nombre)
    {
        string simple = NombreSimple(nombre);

        return simple.StartsWith(
                   "Remove-",
                   StringComparison.OrdinalIgnoreCase)
               && !EsEliminacionTemporalDePowerShell(simple)
               || simple.StartsWith(
                   "Uninstall-",
                   StringComparison.OrdinalIgnoreCase)
               || simple.StartsWith(
                   "Delete-",
                   StringComparison.OrdinalIgnoreCase)
               || simple.Equals(
                   "Clear-Content",
                   StringComparison.OrdinalIgnoreCase)
               || simple.Equals(
                   "Clear-Item",
                   StringComparison.OrdinalIgnoreCase)
               || simple.Equals(
                   "Clear-ItemProperty",
                   StringComparison.OrdinalIgnoreCase)
               || simple.Equals(
                   "Clear-RecycleBin",
                   StringComparison.OrdinalIgnoreCase)
               || simple.Equals(
                   "Clear-EventLog",
                   StringComparison.OrdinalIgnoreCase)
               || simple.Equals("unlink", StringComparison.OrdinalIgnoreCase)
               || simple.Equals("shred", StringComparison.OrdinalIgnoreCase);
    }

    private static bool EsEliminacionTemporalDePowerShell(
        string nombre)
    {
        return nombre.Equals(
                   "Remove-Breakpoint",
                   StringComparison.OrdinalIgnoreCase)
               || nombre.Equals(
                   "Remove-Event",
                   StringComparison.OrdinalIgnoreCase)
               || nombre.Equals(
                   "Remove-Job",
                   StringComparison.OrdinalIgnoreCase)
               || nombre.Equals(
                   "Remove-Module",
                   StringComparison.OrdinalIgnoreCase)
               || nombre.Equals(
                   "Remove-PSBreakpoint",
                   StringComparison.OrdinalIgnoreCase)
               || nombre.Equals(
                   "Remove-PSDrive",
                   StringComparison.OrdinalIgnoreCase)
               || nombre.Equals(
                   "Remove-PSSession",
                   StringComparison.OrdinalIgnoreCase)
               || nombre.Equals(
                   "Remove-TypeData",
                   StringComparison.OrdinalIgnoreCase)
               || nombre.Equals(
                   "Remove-Variable",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool EsMovimiento(string nombre)
    {
        string simple = NombreSimple(nombre);

        return simple.Equals(
                   "Move-Item",
                   StringComparison.OrdinalIgnoreCase)
               || simple.Equals(
                   "Move-ItemProperty",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool EsFormatoDeUnidad(string nombre)
    {
        string simple = NombreSimple(nombre);

        return simple.Equals(
                   "Format-Volume",
                   StringComparison.OrdinalIgnoreCase)
               || simple.Equals(
                   "Clear-Disk",
                   StringComparison.OrdinalIgnoreCase)
               || simple.Equals(
                   "Initialize-Disk",
                   StringComparison.OrdinalIgnoreCase)
               || simple.Equals(
                   "format",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContieneOperacionProhibidaEnInterprete(
        string nombre,
        string texto)
    {
        string simple = NombreSimple(nombre);

        if (simple.Equals("cmd", StringComparison.OrdinalIgnoreCase))
        {
            return Regex.IsMatch(
                texto,
                @"(?:/c|/k)\s+['""]?\s*(?:del|erase|rd|rmdir|move|format|diskpart)\b",
                RegexOptions.IgnoreCase
                | RegexOptions.CultureInvariant);
        }

        if (simple.Equals(
                "powershell",
                StringComparison.OrdinalIgnoreCase)
            || simple.Equals("pwsh", StringComparison.OrdinalIgnoreCase))
        {
            return Regex.IsMatch(
                texto,
                @"\b(?:Remove-(?!(?:Breakpoint|Event|Job|Module|PSBreakpoint|PSDrive|PSSession|TypeData|Variable)\b)[\w-]+|Uninstall-[\w-]+|Clear-Content|Clear-Item|Move-Item(?:Property)?|Format-Volume|Clear-Disk|Initialize-Disk|diskpart)\b",
                RegexOptions.IgnoreCase
                | RegexOptions.CultureInvariant);
        }

        if (simple.Equals("wsl", StringComparison.OrdinalIgnoreCase)
            || simple.Equals("bash", StringComparison.OrdinalIgnoreCase)
            || simple.Equals("sh", StringComparison.OrdinalIgnoreCase))
        {
            return Regex.IsMatch(
                texto,
                @"(?:^|\s)(?:rm|mv|rmdir|unlink|shred|mkfs(?:\.\w+)?)(?:\s|$)",
                RegexOptions.IgnoreCase
                | RegexOptions.CultureInvariant);
        }

        if (simple.Equals("python", StringComparison.OrdinalIgnoreCase)
            || simple.Equals("python3", StringComparison.OrdinalIgnoreCase)
            || simple.Equals("py", StringComparison.OrdinalIgnoreCase))
        {
            return Regex.IsMatch(
                texto,
                @"\b(?:os\.(?:remove|unlink|rmdir|rename|replace)|shutil\.(?:rmtree|move)|pathlib[^\r\n;]*\.(?:unlink|rmdir|rename|replace))\s*\(",
                RegexOptions.IgnoreCase
                | RegexOptions.CultureInvariant);
        }

        if (simple.Equals("node", StringComparison.OrdinalIgnoreCase)
            || simple.Equals("deno", StringComparison.OrdinalIgnoreCase))
        {
            return Regex.IsMatch(
                texto,
                @"\b(?:fs\.)?(?:unlink|rm|rmdir|rename)\s*\(",
                RegexOptions.IgnoreCase
                | RegexOptions.CultureInvariant);
        }

        if (simple.Equals("ruby", StringComparison.OrdinalIgnoreCase)
            || simple.Equals("perl", StringComparison.OrdinalIgnoreCase))
        {
            return Regex.IsMatch(
                texto,
                @"\b(?:File\.)?(?:delete|unlink|rename)\s*\(",
                RegexOptions.IgnoreCase
                | RegexOptions.CultureInvariant);
        }

        if (simple.Equals("Add-Type", StringComparison.OrdinalIgnoreCase))
        {
            return Regex.IsMatch(
                texto,
                @"\b(?:File|Directory)\.(?:Delete|Move|Replace)\s*\(",
                RegexOptions.IgnoreCase
                | RegexOptions.CultureInvariant);
        }

        return false;
    }

    private static bool ContieneOperacionProhibidaEnUtilidad(
        string nombre,
        string texto)
    {
        string simple = NombreSimple(nombre);

        if (simple.Equals("robocopy", StringComparison.OrdinalIgnoreCase))
        {
            return Regex.IsMatch(
                texto,
                @"(?:^|\s)/(?:MOV|MOVE|MIR|PURGE)\b",
                RegexOptions.IgnoreCase
                | RegexOptions.CultureInvariant);
        }

        if (simple.Equals("git", StringComparison.OrdinalIgnoreCase))
        {
            return Regex.IsMatch(
                texto,
                @"\bgit(?:\.exe)?\s+(?:clean|rm|mv)\b",
                RegexOptions.IgnoreCase
                | RegexOptions.CultureInvariant);
        }

        if ((simple.Equals("winget", StringComparison.OrdinalIgnoreCase)
             || simple.Equals("choco", StringComparison.OrdinalIgnoreCase)
             || simple.Equals("scoop", StringComparison.OrdinalIgnoreCase)
             || simple.Equals("npm", StringComparison.OrdinalIgnoreCase)
             || simple.Equals("pnpm", StringComparison.OrdinalIgnoreCase)
             || simple.Equals("yarn", StringComparison.OrdinalIgnoreCase))
            && Regex.IsMatch(
                texto,
                @"\s(?:uninstall|remove|purge)\b",
                RegexOptions.IgnoreCase
                | RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (simple.Equals("diskpart", StringComparison.OrdinalIgnoreCase))
        {
            return Regex.IsMatch(
                texto,
                @"(?:^|\s)/s\b|\b(?:format|clean|delete)\b",
                RegexOptions.IgnoreCase
                | RegexOptions.CultureInvariant);
        }

        return (simple.Equals("reg", StringComparison.OrdinalIgnoreCase)
                || simple.Equals("sc", StringComparison.OrdinalIgnoreCase)
                || simple.Equals(
                    "vssadmin",
                    StringComparison.OrdinalIgnoreCase)
                || simple.Equals(
                    "wbadmin",
                    StringComparison.OrdinalIgnoreCase)
                || simple.Equals(
                    "diskshadow",
                    StringComparison.OrdinalIgnoreCase))
               && Regex.IsMatch(
                   texto,
                   @"\s(?:delete|remove)\b",
                   RegexOptions.IgnoreCase
                   | RegexOptions.CultureInvariant)
               || simple.Equals(
                      "schtasks",
                      StringComparison.OrdinalIgnoreCase)
                  && Regex.IsMatch(
                      texto,
                      @"(?:^|\s)/delete\b",
                      RegexOptions.IgnoreCase
                      | RegexOptions.CultureInvariant)
               || simple.Equals(
                      "cipher",
                      StringComparison.OrdinalIgnoreCase)
                  && Regex.IsMatch(
                      texto,
                      @"(?:^|\s)/w(?::|\s|$)",
                      RegexOptions.IgnoreCase
                      | RegexOptions.CultureInvariant);
    }

    private static bool ContieneLanzamientoAnidadoProhibido(
        string texto)
    {
        return Regex.IsMatch(
            texto,
            @"\b(?:powershell|pwsh|cmd|wsl|bash|sh|python|python3|py|node)(?:\.exe)?\b[^\r\n;|]*(?:Remove-(?!(?:Breakpoint|Event|Job|Module|PSBreakpoint|PSDrive|PSSession|TypeData|Variable)\b)[\w-]+|Uninstall-[\w-]+|Clear-Content|Move-Item(?:Property)?|Format-Volume|Clear-Disk|Initialize-Disk|diskpart|\bdel\b|\berase\b|\brmdir\b|\bos\.(?:remove|unlink|rmdir|rename|replace)\b|\bshutil\.(?:rmtree|move)\b)",
            RegexOptions.IgnoreCase
            | RegexOptions.CultureInvariant);
    }

    private static bool EsMetodoEliminacion(string miembro)
    {
        return miembro.Equals("Delete", StringComparison.OrdinalIgnoreCase)
               || miembro.Equals(
                   "DeleteFile",
                   StringComparison.OrdinalIgnoreCase)
               || miembro.Equals(
                   "DeleteFolder",
                   StringComparison.OrdinalIgnoreCase)
               || miembro.Equals("Unlink", StringComparison.OrdinalIgnoreCase)
               || miembro.Equals("Rmdir", StringComparison.OrdinalIgnoreCase)
               || miembro.Equals(
                   "RemoveDirectory",
                   StringComparison.OrdinalIgnoreCase)
               || miembro.Equals(
                   "Truncate",
                   StringComparison.OrdinalIgnoreCase)
               || miembro.Equals(
                   "RegDelete",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool EsMetodoMovimiento(
        InvokeMemberExpressionAst invocacion,
        string miembro)
    {
        if (miembro.Equals(
                   "MoveFile",
                   StringComparison.OrdinalIgnoreCase)
            || miembro.Equals(
                   "MoveFolder",
                   StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!miembro.Equals(
                "Move",
                StringComparison.OrdinalIgnoreCase)
            && !miembro.Equals(
                "MoveTo",
                StringComparison.OrdinalIgnoreCase)
            && !miembro.Equals(
                "Replace",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string receptor = invocacion.Expression.Extent.Text;

        if (PareceReceptorSistemaArchivos(receptor))
        {
            return true;
        }

        if (invocacion.Expression is not VariableExpressionAst variable)
        {
            return false;
        }

        Ast raiz = invocacion;

        while (raiz.Parent is not null)
        {
            raiz = raiz.Parent;
        }

        string nombreVariable =
            Regex.Escape(variable.VariablePath.UserPath);

        return Regex.IsMatch(
            raiz.Extent.Text,
            $@"\${nombreVariable}\s*=\s*[^\r\n;]*(?:Get-Item|Get-ChildItem|\[(?:System\.)?IO\.(?:File|Directory|FileInfo|DirectoryInfo)\])",
            RegexOptions.IgnoreCase
            | RegexOptions.CultureInvariant);
    }

    private static bool PareceReceptorSistemaArchivos(string receptor)
    {
        return Regex.IsMatch(
            receptor,
            @"\[(?:System\.)?IO\.(?:File|Directory|FileInfo|DirectoryInfo)\]|\b(?:Get-Item|Get-ChildItem)\b",
            RegexOptions.IgnoreCase
            | RegexOptions.CultureInvariant);
    }

    private static string NormalizarAlias(string nombre)
    {
        string simple = NombreSimple(nombre);
        return Alias.TryGetValue(simple, out string? expandido)
            ? expandido
            : simple;
    }

    private static string NombreSimple(string nombre)
    {
        string archivo = Path.GetFileName(nombre.Trim());
        string sinExtension = Path.GetFileNameWithoutExtension(archivo);

        return string.IsNullOrWhiteSpace(sinExtension)
            ? archivo
            : sinExtension;
    }

    private static string? ObtenerNombreMiembro(Ast miembro) =>
        miembro switch
        {
            StringConstantExpressionAst cadena => cadena.Value,
            ConstantExpressionAst constante =>
                constante.Value?.ToString(),
            _ => null
        };

    internal static IReadOnlyList<string>
        ObtenerRaicesBusquedaPermitidas()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(unidad =>
                    unidad.IsReady
                    && unidad.DriveType == DriveType.Fixed)
                .Select(unidad =>
                    Path.GetFullPath(
                        unidad.RootDirectory.FullName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (IOException)
        {
            return ObtenerRaizPerfilActual();
        }
        catch (UnauthorizedAccessException)
        {
            return ObtenerRaizPerfilActual();
        }
    }

    private static IReadOnlyList<string> ObtenerRaizPerfilActual()
    {
        string perfil = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);
        string? raiz = string.IsNullOrWhiteSpace(perfil)
            ? null
            : Path.GetPathRoot(
                Path.GetFullPath(perfil));

        return string.IsNullOrWhiteSpace(raiz)
            ? []
            : [raiz];
    }

    private static ResultadoValidacionPowerShell Bloquear(
        string motivo) =>
        new(false, motivo);
}
