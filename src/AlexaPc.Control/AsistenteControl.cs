namespace ControlPCIA;

/// <summary>
/// Da prioridad al núcleo determinista y sólo consulta al modelo local cuando
/// esa ruta no puede traducir la petición. El modelo nunca ejecuta comandos.
/// </summary>
internal static class AsistenteControl
{
    private static readonly HashSet<string> EstadosConRespaldo =
        new(StringComparer.Ordinal)
        {
            "no_disponible",
            "aplicacion_no_encontrada"
        };

    public static EstadoControlBasico Estado { get; } =
        new(
            true,
            "control-hibrido-local",
            $"ControlPCIA está preparado. Las funciones conocidas son inmediatas y {ClienteOllama.Modelo} traduce localmente las demás órdenes.");

    public static async Task<ResultadoControl> ControlarAsync(
        string instruccion,
        IReadOnlyList<MensajeConversacionControl>? contexto = null,
        CancellationToken cancellationToken = default,
        bool soloTraducir = false)
    {
        ResultadoControl basico =
            await ControlBasico.ControlarAsync(
                instruccion,
                cancellationToken,
                soloTraducir);

        if (!EstadosConRespaldo.Contains(
                basico.Estado))
        {
            return basico;
        }

        return await TraductorLocalRapido.ControlarAsync(
            instruccion,
            contexto,
            cancellationToken,
            soloTraducir);
    }
}
