using System.Collections.Concurrent;
using System.Management.Automation.Language;

namespace ControlPCIA;

/// <summary>
/// Comprueba con Get-Command que los nombres estáticos propuestos por el
/// modelo existen antes de ejecutar una acción compuesta. Así una invención
/// en el segundo comando no deja ejecutada sólo la primera mitad.
/// </summary>
internal static class ComprobadorComandosPowerShell
{
    private static readonly ConcurrentDictionary<
        string,
        EntradaCache> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static async Task<IReadOnlyList<string>>
        ObtenerNoDisponiblesAsync(
            string comando,
            CancellationToken cancellationToken = default)
    {
        string[] nombres =
            ObtenerNombresEstaticos(comando);

        if (nombres.Length == 0)
        {
            return [];
        }

        DateTimeOffset ahora = DateTimeOffset.UtcNow;
        var noDisponibles = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        string[] pendientes = nombres
            .Where(nombre =>
            {
                if (!Cache.TryGetValue(
                        nombre,
                        out EntradaCache? entrada)
                    || entrada.Hasta <= ahora)
                {
                    return true;
                }

                if (!entrada.Disponible)
                {
                    noDisponibles.Add(nombre);
                }

                return false;
            })
            .ToArray();

        if (pendientes.Length == 0)
        {
            return noDisponibles.ToArray();
        }

        string lista = string.Join(
            ',',
            pendientes.Select(nombre =>
                "'" + nombre.Replace(
                    "'",
                    "''",
                    StringComparison.Ordinal) + "'"));
        string consulta =
            "$nombres = @(" + lista + "); "
            + "foreach ($nombre in $nombres) { "
            + "if ($null -eq (Get-Command -Name $nombre -ErrorAction SilentlyContinue)) { "
            + "Write-Output $nombre } }";
        ResultadoEjecucionPowerShell resultado =
            await EjecutorPowerShell.EjecutarAsync(
                consulta,
                cancellationToken);

        if (!resultado.Ejecutado
            || resultado.CodigoSalida != 0
            || !string.IsNullOrWhiteSpace(resultado.Error))
        {
            // Es una comprobación de calidad. Si PowerShell no puede
            // comprobarla, la validación normal y la ejecución siguen.
            return noDisponibles.ToArray();
        }

        var ausentes = new HashSet<string>(
            resultado.Salida.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

        foreach (string nombre in pendientes)
        {
            bool disponible = !ausentes.Contains(nombre);
            Cache[nombre] = new EntradaCache(
                disponible,
                ahora + (disponible
                    ? TimeSpan.FromMinutes(30)
                    : TimeSpan.FromSeconds(30)));

            if (!disponible)
            {
                noDisponibles.Add(nombre);
            }
        }

        return noDisponibles.ToArray();
    }

    internal static string[] ObtenerNombresEstaticos(
        string comando)
    {
        ScriptBlockAst ast = Parser.ParseInput(
            comando,
            out _,
            out ParseError[] errores);

        if (errores.Length > 0)
        {
            return [];
        }

        var funcionesDeclaradas = ast
            .FindAll(
                nodo => nodo is FunctionDefinitionAst,
                searchNestedScriptBlocks: true)
            .Cast<FunctionDefinitionAst>()
            .Select(funcion => funcion.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return ast
            .FindAll(
                nodo => nodo is CommandAst,
                searchNestedScriptBlocks: true)
            .Cast<CommandAst>()
            .Select(comandoAst =>
                comandoAst.GetCommandName())
            .Where(nombre =>
                !string.IsNullOrWhiteSpace(nombre)
                && !funcionesDeclaradas.Contains(nombre))
            .Select(nombre => nombre!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static void InvalidarCache()
    {
        Cache.Clear();
    }

    private sealed record EntradaCache(
        bool Disponible,
        DateTimeOffset Hasta);
}
