using Android.App;
using Android.Media;
using Android.Runtime;
using Android.Util;

namespace Bardo.Mobile;

[Application]
public sealed class BardoApplication : Application
{
    private const string LogTag = "BardoFeedback";

    private ToneGenerator? _commandAcknowledgementTone;

    public BardoApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
    }

    public override void OnCreate()
    {
        base.OnCreate();
        BardoVoiceService.StatusChanged += HandleVoiceStatusChanged;
    }

    public override void OnTerminate()
    {
        BardoVoiceService.StatusChanged -= HandleVoiceStatusChanged;
        ReleaseCommandAcknowledgementTone();
        base.OnTerminate();
    }

    private void HandleVoiceStatusChanged(string status)
    {
        if (!status.StartsWith("Ejecutando:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        PlayCommandAcknowledgement();
    }

    private void PlayCommandAcknowledgement()
    {
        try
        {
            // El primer tono (PropAck) confirma la wake word. Este segundo tono,
            // distinto y más corto, confirma que la orden ya ha sido entendida y
            // enviada al PC.
            _commandAcknowledgementTone ??= new ToneGenerator(Stream.Alarm, 85);
            _commandAcknowledgementTone.StopTone();
            _commandAcknowledgementTone.StartTone(Tone.PropBeep, 130);
            Log.Info(LogTag, "Tono de orden entendida reproducido");
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, $"No se pudo reproducir el tono de orden entendida: {ex}");
        }
    }

    private void ReleaseCommandAcknowledgementTone()
    {
        try
        {
            _commandAcknowledgementTone?.StopTone();
            _commandAcknowledgementTone?.Release();
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, $"No se pudo liberar el tono de orden entendida: {ex}");
        }
        finally
        {
            _commandAcknowledgementTone?.Dispose();
            _commandAcknowledgementTone = null;
        }
    }
}
