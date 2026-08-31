using Xunit;

namespace ControlPCIA.Tests;

public sealed class ValidadorPowerShellTests
{
    public static TheoryData<string> OperacionesNormalesPermitidas => new()
    {
        "Get-Process | Select-Object Name,Id,MainWindowTitle",
        "Get-Content 'C:\\Documentos\\informe.txt'",
        "Select-String -Path 'C:\\Documentos\\*.txt' -Pattern 'dato'",
        "Get-ChildItem -LiteralPath 'C:\\' -File -Recurse -ErrorAction SilentlyContinue",
        "New-Item -Path 'C:\\Documentos\\Proyecto' -ItemType Directory -Force",
        "Copy-Item -LiteralPath 'C:\\Plantillas\\base.cpr' -Destination 'C:\\Documentos\\Proyecto\\nuevo.cpr' -Force",
        "Rename-Item -LiteralPath 'C:\\Documentos\\antes.txt' -NewName 'despues.txt'",
        "Set-Content -LiteralPath 'C:\\Documentos\\nota.txt' -Value 'texto'",
        "Add-Content -LiteralPath 'C:\\Documentos\\nota.txt' -Value 'más texto'",
        "Get-Process > 'C:\\Documentos\\procesos.txt'",
        "Compress-Archive -Path 'C:\\Documentos\\Proyecto' -DestinationPath 'C:\\Documentos\\Proyecto.zip' -Force",
        "Expand-Archive -LiteralPath 'C:\\Documentos\\Proyecto.zip' -DestinationPath 'C:\\Documentos\\Copia' -Force",
        "Invoke-WebRequest 'https://example.com/programa.exe' -OutFile 'C:\\Descargas\\programa.exe'",
        "Invoke-RestMethod 'https://example.com/api'",
        "Start-BitsTransfer -Source 'https://example.com/archivo.zip' -Destination 'C:\\Descargas\\archivo.zip'",
        "Start-Process -FilePath 'C:\\Program Files\\Aplicacion\\Aplicacion.exe' -ArgumentList '--new-project','C:\\Documentos\\Proyecto'",
        "Start-Process -FilePath 'C:\\Documentos\\informe.pdf'",
        "Start-Process -FilePath 'C:\\Documentos\\carta.docx'",
        "Start-Process -FilePath 'C:\\Documentos\\Proyecto\\cancion.cpr'",
        "Invoke-Item -LiteralPath 'C:\\Documentos\\informe.pdf'",
        "$destino = 'notepad'; Start-Process $destino",
        "Start-Process 'http://127.0.0.1:8080/admin'",
        "Start-Process 'file:///C:/Documentos/informe.pdf'",
        "winget install --id Microsoft.PowerToys --exact --source winget",
        "Import-Module AudioDeviceCmdlets",
        "Add-Type -AssemblyName System.Windows.Forms",
        "New-ItemProperty -Path 'HKCU:\\Software\\ControlPCIA' -Name Activo -Value 1 -Force",
        "Set-ItemProperty -Path 'HKCU:\\Software\\ControlPCIA' -Name Activo -Value 1",
        "Set-Service -Name Audiosrv -StartupType Automatic",
        "Restart-Service -Name Audiosrv",
        "Register-ScheduledTask -TaskName ControlPCIA -Action (New-ScheduledTaskAction -Execute 'ControlPCIA.exe')",
        "New-NetFirewallRule -DisplayName ControlPCIA -Direction Inbound -Action Allow",
        "Set-NetIPInterface -InterfaceAlias Ethernet -Dhcp Enabled",
        "Set-MpPreference -DisableRealtimeMonitoring $false",
        "Set-ExecutionPolicy RemoteSigned -Scope CurrentUser",
        "Stop-Computer",
        "Restart-Computer",
        "Set-TimeZone -Id 'Romance Standard Time'",
        "Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V -All",
        "Set-CimInstance -Query \"SELECT * FROM Win32_Environment WHERE Name='X'\" -Property @{ VariableValue = 'Y' }",
        "Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = 'notepad.exe' }",
        "cmd.exe /c echo ControlPCIA",
        "powershell.exe -NoProfile -Command \"Get-Date\"",
        "powershell.exe -NoProfile -Command \"Move-Window -ProcessName notepad -X 100 -Y 100\"",
        "powershell.exe -NoProfile -Command \"Remove-Variable -Name temporal -ErrorAction SilentlyContinue\"",
        "Start-Process powershell.exe -ArgumentList '-NoProfile','-Command','Move-Window -ProcessName notepad -X 100 -Y 100'",
        "python -c \"print('ControlPCIA')\"",
        "node -e \"console.log('ControlPCIA')\"",
        "Remove-Variable -Name temporal -ErrorAction SilentlyContinue",
        "Remove-Job -Id 1 -Force",
        "Move-Window -ProcessName notepad -X 100 -Y 100",
        "Set-DisplayResolution -Width 1920 -Height 1080 -Force",
        "Set-DisplayScale -Scale 125",
        "$texto = 'Cubase'; $texto.Replace('u','o')",
        "git status --short",
        "diskpart",
        "robocopy 'C:\\Origen' 'C:\\Destino' /E",
        "[System.IO.File]::WriteAllText('C:\\Documentos\\nota.txt','texto')",
        "[System.IO.File]::Copy('C:\\Plantillas\\base.cpr','C:\\Documentos\\nuevo.cpr',$true)",
        "$excel = New-Object -ComObject Excel.Application; $libro = $excel.Workbooks.Add(); $libro.SaveAs('C:\\Documentos\\Libro.xlsx')",
        "Save-CubaseProject -Name 'Prueba de voz'",
        "DisplaySwitch.exe /extend"
    };

    public static TheoryData<string> TresProhibicionesYEvasiones => new()
    {
        "Remove-Item 'C:\\Documentos\\nota.txt'",
        "rm 'C:\\Documentos\\nota.txt'",
        "del 'C:\\Documentos\\nota.txt'",
        "Clear-Content 'C:\\Documentos\\nota.txt'",
        "Remove-LocalUser -Name usuario",
        "winget uninstall --id Microsoft.PowerToys",
        "Move-Item 'C:\\Origen\\nota.txt' 'C:\\Destino\\nota.txt'",
        "Move-ItemProperty -Path 'HKCU:\\Software\\Origen' -Name Valor -Destination 'HKCU:\\Software\\Destino'",
        "mv 'C:\\Origen\\nota.txt' 'C:\\Destino\\nota.txt'",
        "Format-Volume -DriveLetter D",
        "Clear-Disk -Number 1 -RemoveData",
        "Initialize-Disk -Number 1",
        "diskpart /s 'C:\\Scripts\\disco.txt'",
        "format.com D: /Q",
        "cmd.exe /c del C:\\Documentos\\nota.txt",
        "powershell.exe -Command \"Remove-Item 'C:\\Documentos\\nota.txt'\"",
        "python -c \"import os; os.remove('C:/Documentos/nota.txt')\"",
        "python -c \"import shutil; shutil.move('C:/Origen','C:/Destino')\"",
        "[System.IO.File]::Delete('C:\\Documentos\\nota.txt')",
        "[System.IO.File]::Move('C:\\Origen\\nota.txt','C:\\Destino\\nota.txt')",
        "[System.IO.File]::Replace('C:\\Nuevo.txt','C:\\Actual.txt','C:\\Copia.txt')",
        "$archivo = [System.IO.FileInfo]::new('C:\\Origen\\nota.txt'); $archivo.MoveTo('C:\\Destino\\nota.txt')",
        "$archivo = Get-Item 'C:\\Documentos\\nota.txt'; $archivo.Delete()",
        "Invoke-CimMethod -ClassName Win32_Volume -MethodName Format",
        "robocopy 'C:\\Origen' 'C:\\Destino' /MOVE",
        "robocopy 'C:\\Origen' 'C:\\Destino' /MIR",
        "git clean -fd",
        "git rm archivo.txt",
        "git mv antes.txt despues.txt",
        "& ('Remove-' + 'Item') 'C:\\Documentos\\nota.txt'",
        "powershell.exe -EncodedCommand ZABlAGwA",
        "ControlPCIA.exe ui windows",
        "$shell = New-Object -ComObject WScript.Shell; $shell.SendKeys('texto')",
        "[System.Windows.Forms.SendKeys]::SendWait('%{F4}')"
    };

    [Theory]
    [MemberData(nameof(OperacionesNormalesPermitidas))]
    public void Permite_todo_lo_que_no_sea_una_de_las_tres_prohibiciones(
        string comando)
    {
        ResultadoValidacionPowerShell resultado =
            ValidadorPowerShell.Validar(comando);

        Assert.True(resultado.Permitido, resultado.Motivo);
    }

    [Theory]
    [MemberData(nameof(TresProhibicionesYEvasiones))]
    public void Bloquea_solo_eliminar_mover_formatear_y_sus_evasiones(
        string comando)
    {
        ResultadoValidacionPowerShell resultado =
            ValidadorPowerShell.Validar(comando);

        Assert.False(resultado.Permitido, comando);
        Assert.NotEmpty(resultado.Motivo);
    }

    [Fact]
    public void Bloquea_comandos_vacios_y_sintaxis_invalida()
    {
        Assert.False(
            ValidadorPowerShell.Validar("   ").Permitido);

        ResultadoValidacionPowerShell invalido =
            ValidadorPowerShell.Validar("Get-Process | {");

        Assert.False(invalido.Permitido);
        Assert.Contains(
            "sintaxis",
            invalido.Motivo,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void La_autorizacion_no_reactiva_la_interfaz_grafica_retirada()
    {
        const string comando =
            "ControlPCIA.exe ui invoke 'Word' 'Save As' 'MenuItem'";

        Assert.False(
            ValidadorPowerShell.Validar(comando).Permitido);
        Assert.False(
            ValidadorPowerShell.Validar(
                comando,
                permitirDescarte: true).Permitido);
    }

    [Theory]
    [InlineData("$shell = New-Object -ComObject WScript.Shell; $shell.AppActivate('Microsoft Edge')")]
    [InlineData("[Ventanas]::ShowWindowAsync($p.MainWindowHandle, 3)")]
    [InlineData("[Ventanas]::SetForegroundWindow($p.MainWindowHandle)")]
    [InlineData("[Ventanas]::SetWindowPos($p.MainWindowHandle, [IntPtr]::Zero, 0, 0, 1200, 800, 0)")]
    public void Permite_controlar_el_estado_de_ventanas_superiores(
        string comando)
    {
        Assert.True(
            ValidadorPowerShell.Validar(comando).Permitido);
    }

    [Fact]
    public void Mantiene_disponibles_las_unidades_locales_para_busquedas()
    {
        Assert.NotEmpty(
            ValidadorPowerShell.ObtenerRaicesBusquedaPermitidas());
    }
}
