namespace ControlPCIA;

public sealed record GeneralControlResult(
    bool Completed,
    string State,
    string Message,
    int StepCount);

public static class GeneralComputerController
{
    public static async Task<GeneralControlResult> ExecuteAsync(
        string instruction,
        CancellationToken cancellationToken = default)
    {
        var result = await AsistenteControl
            .ControlarAsync(instruction, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return new GeneralControlResult(
            result.Completado,
            result.Estado,
            result.Mensaje,
            result.Pasos.Count);
    }

    public static Task WarmUpAsync(CancellationToken cancellationToken = default)
        => Task.WhenAll(
            InventarioAplicaciones.PrecalentarAsync(cancellationToken),
            TraductorLocalRapido.PrecalentarAsync(cancellationToken));
}
