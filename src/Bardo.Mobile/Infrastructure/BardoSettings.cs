using Android.Content;

namespace Bardo.Mobile.Infrastructure;

internal sealed record BardoSettings(
    string RelayUrl,
    string ApiKey,
    string DeviceId,
    string WakeWord)
{
    public static BardoSettings Default { get; } = new(
        "http://192.168.1.2:5184",
        "dev-api-key",
        "pc-principal",
        "bardo");
}

internal static class BardoSettingsStore
{
    private const string PreferencesName = "bardo-settings";
    private const string RelayUrlKey = "relay-url";
    private const string ApiKeyKey = "api-key";
    private const string DeviceIdKey = "device-id";
    private const string WakeWordKey = "wake-word";

    public static BardoSettings Load(Context context)
    {
        var preferences = context.GetSharedPreferences(PreferencesName, FileCreationMode.Private);
        var defaults = BardoSettings.Default;

        return new BardoSettings(
            preferences?.GetString(RelayUrlKey, defaults.RelayUrl) ?? defaults.RelayUrl,
            preferences?.GetString(ApiKeyKey, defaults.ApiKey) ?? defaults.ApiKey,
            preferences?.GetString(DeviceIdKey, defaults.DeviceId) ?? defaults.DeviceId,
            preferences?.GetString(WakeWordKey, defaults.WakeWord) ?? defaults.WakeWord);
    }

    public static void Save(Context context, BardoSettings settings)
    {
        var preferences = context.GetSharedPreferences(PreferencesName, FileCreationMode.Private);
        var editor = preferences?.Edit();
        if (editor is null)
        {
            return;
        }

        editor.PutString(RelayUrlKey, settings.RelayUrl.Trim());
        editor.PutString(ApiKeyKey, settings.ApiKey.Trim());
        editor.PutString(DeviceIdKey, settings.DeviceId.Trim());
        editor.PutString(WakeWordKey, settings.WakeWord.Trim().ToLowerInvariant());
        editor.Apply();
    }
}
