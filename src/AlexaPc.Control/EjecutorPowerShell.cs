using System.Diagnostics;

using System.IO;
using System.Text;

namespace ControlPCIA
{
    internal sealed record ResultadoEjecucionPowerShell(
        bool Ejecutado,
        int CodigoSalida,
        string Salida,
        string Error);

    internal static class EjecutorPowerShell
    {
        private static readonly TimeSpan TiempoMaximo =
            TimeSpan.FromMinutes(10);

        private const int CaracteresMaximosSalida = 64_000;

        public static async Task<ResultadoEjecucionPowerShell>
            EjecutarAsync(
                string comando,
                CancellationToken cancellationToken = default,
                bool permitirDescarte = false)
        {
            ResultadoValidacionPowerShell validacion =
                ValidadorPowerShell.Validar(
                    comando,
                    permitirDescarte);

            if (!validacion.Permitido)
            {
                return new(
                    false,
                    -1,
                    "",
                    "BLOQUEADO: " +
                    validacion.Motivo);
            }

            var inicio =
                new ProcessStartInfo
                {
                    FileName =
                        "powershell.exe",

                    UseShellExecute =
                        false,

                    RedirectStandardOutput =
                        true,

                    RedirectStandardError =
                        true,

                    StandardOutputEncoding =
                        new UTF8Encoding(
                            encoderShouldEmitUTF8Identifier: false),

                    StandardErrorEncoding =
                        new UTF8Encoding(
                            encoderShouldEmitUTF8Identifier: false),

                    CreateNoWindow =
                        true,

                    WorkingDirectory =
                        AppContext.BaseDirectory
                };

            string pathActual =
                Environment.GetEnvironmentVariable("PATH")
                ?? string.Empty;
            inicio.Environment["PATH"] =
                AppContext.BaseDirectory
                + Path.PathSeparator
                + pathActual;

            if (permitirDescarte)
            {
                inicio.Environment["CONTROLPCIA_PERMITIR_DESCARTE"] =
                    "1";
            }
            else
            {
                inicio.Environment.Remove(
                    "CONTROLPCIA_PERMITIR_DESCARTE");
            }

            inicio.ArgumentList.Add(
                "-NoLogo");

            inicio.ArgumentList.Add(
                "-NoProfile");

            inicio.ArgumentList.Add(
                "-NonInteractive");

            inicio.ArgumentList.Add(
                "-Command");

            inicio.ArgumentList.Add(
                "[Console]::OutputEncoding = "
                + "[System.Text.UTF8Encoding]::new($false); "
                + comando);

            using var proceso =
                new Process
                {
                    StartInfo = inicio
                };

            try
            {
                if (!proceso.Start())
                {
                    return new(
                        false,
                        -1,
                        "",
                        "No se pudo iniciar PowerShell.");
                }

                Task<string> salidaTask = LeerLimitadoAsync(
                    proceso.StandardOutput,
                    CaracteresMaximosSalida);

                Task<string> errorTask = LeerLimitadoAsync(
                    proceso.StandardError,
                    CaracteresMaximosSalida);

                using var limite =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);

                limite.CancelAfter(TiempoMaximo);

                try
                {
                    await proceso.WaitForExitAsync(limite.Token);
                }
                catch (OperationCanceledException)
                {
                    try
                    {
                        proceso.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // El proceso puede haber terminado entre la espera
                        // y el intento de cancelación.
                    }

                    await Task.WhenAll(salidaTask, errorTask);

                    cancellationToken.ThrowIfCancellationRequested();

                    return new(
                        false,
                        -1,
                        (await salidaTask).Trim(),
                        $"La consulta superó el límite de " +
                        $"{TiempoMaximo.TotalSeconds:0} segundos.");
                }

                string salida =
                    await salidaTask;

                string error =
                    await errorTask;

                return new(
                    true,
                    proceso.ExitCode,
                    salida.Trim(),
                    error.Trim());
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new(
                    false,
                    -1,
                    "",
                    ex.Message);
            }
        }

        private static async Task<string> LeerLimitadoAsync(
            StreamReader lector,
            int limite)
        {
            var resultado = new StringBuilder(
                Math.Min(limite, 4096));

            char[] buffer = new char[4096];
            bool recortado = false;

            while (true)
            {
                int leidos = await lector.ReadAsync(buffer);

                if (leidos == 0)
                {
                    break;
                }

                int disponibles = limite - resultado.Length;

                if (disponibles > 0)
                {
                    int copiar = Math.Min(disponibles, leidos);
                    resultado.Append(buffer, 0, copiar);
                    recortado |= copiar < leidos;
                }
                else
                {
                    recortado = true;
                }
            }

            if (recortado)
            {
                resultado.AppendLine();
                resultado.Append("[Salida recortada por seguridad]");
            }

            return resultado.ToString();
        }
    }
}
