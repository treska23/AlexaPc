using Android.Content;

namespace Bardo.Mobile.Infrastructure;

internal static class LegacyLocalSpeechModelCleaner
{
    public static void Clean(Context context)
    {
        try
        {
            string root = Path.Combine(context.FilesDir!.AbsolutePath, "models");
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
            // La limpieza de modelos antiguos no debe impedir que arranque la voz.
        }
    }
}
