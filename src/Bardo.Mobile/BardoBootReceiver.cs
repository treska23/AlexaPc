using Android.App;
using Android.Content;
using Android.Util;

namespace Bardo.Mobile;

[BroadcastReceiver(
    Name = "com.treska23.bardo.BardoBootReceiver",
    Enabled = true,
    Exported = true)]
[IntentFilter([Intent.ActionBootCompleted, Intent.ActionMyPackageReplaced])]
public sealed class BardoBootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null)
        {
            return;
        }

        try
        {
            var voiceIntent = new Intent(context, typeof(BardoVoiceService));
            if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.O)
            {
                context.StartForegroundService(voiceIntent);
            }
            else
            {
                context.StartService(voiceIntent);
            }

            Log.Info(
                "BardoDedicated",
                $"Voz de Bardo iniciada por {intent?.Action ?? "evento del sistema"}");
        }
        catch (Exception ex)
        {
            // En Android reciente el arranque de un servicio de micrófono desde el
            // receptor puede estar limitado hasta que Bardo sea Device Owner. La
            // actividad HOME de abajo vuelve a intentarlo desde primer plano.
            Log.Warn("BardoDedicated", $"Inicio directo de voz aplazado: {ex}");
        }

        try
        {
            var launchIntent = new Intent(context, typeof(MainActivity));
            launchIntent.AddFlags(
                ActivityFlags.NewTask |
                ActivityFlags.ClearTop |
                ActivityFlags.SingleTop);
            launchIntent.PutExtra("bardo_auto_start", true);
            context.StartActivity(launchIntent);
            Log.Info(
                "BardoDedicated",
                $"Interfaz de Bardo abierta por {intent?.Action ?? "evento del sistema"}");
        }
        catch (Exception ex)
        {
            Log.Error("BardoDedicated", $"No se pudo abrir Bardo automáticamente: {ex}");
        }
    }
}
