namespace ControlPCIA;

internal sealed record EstadoControlBasico(
    bool Disponible,
    string Modo,
    string Mensaje);

internal sealed record ResultadoPasoControl(
    int Numero,
    string Comando,
    bool Ejecutado,
    int CodigoSalida,
    string Salida,
    string Error);

internal sealed record EventoControl(
    string Tipo,
    string Mensaje,
    string? Comando = null,
    ResultadoPasoControl? Paso = null);

internal sealed record MensajeConversacionControl(
    string Rol,
    string Texto);

internal sealed record ResultadoControl(
    bool Completado,
    string Estado,
    string Mensaje,
    IReadOnlyList<ResultadoPasoControl> Pasos,
    bool Aprendido);
