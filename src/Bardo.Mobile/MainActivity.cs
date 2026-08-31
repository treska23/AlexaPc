using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Speech;
using Android.Widget;
using Bardo.Mobile.Infrastructure;

namespace Bardo.Mobile;

[Activity(
    Label = "Bardo",
    MainLauncher = true,
    Exported = true,
    ScreenOrientation = ScreenOrientation.Portrait)]
public sealed class MainActivity : Activity
{
    private const int PermissionRequestCode = 1001;

    private EditText? _relayUrl;
    private EditText? _apiKey;
    private EditText? _deviceId;
    private EditText? _wakeWord;
    private EditText? _testCommand;
    private TextView? _status;
    private bool _startAfterPermission;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var settings = BardoSettingsStore.Load(this);
        var root = new ScrollView(this);
        var content = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical
        };

        content.SetPadding(Dp(20), Dp(28), Dp(20), Dp(28));
        root.AddView(content);

        var title = new TextView(this)
        {
            Text = "BARDO",
            TextSize = 30f
        };
        content.AddView(title);

        var subtitle = new TextView(this)
        {
            Text = "Control de voz dedicado para el PC",
            TextSize = 16f
        };
        subtitle.SetPadding(0, 0, 0, Dp(20));
        content.AddView(subtitle);

        _status = new TextView(this)
        {
            Text = "Estado: detenido",
            TextSize = 16f
        };
        _status.SetPadding(0, 0, 0, Dp(18));
        content.AddView(_status);

        _relayUrl = AddField(content, "Relay del PC", settings.RelayUrl);
        _apiKey = AddField(content, "API key", settings.ApiKey);
        _deviceId = AddField(content, "Device ID", settings.DeviceId);
        _wakeWord = AddField(content, "Palabra de activación", settings.WakeWord);
        _testCommand = AddField(content, "Comando de prueba", "abre YouTube");

        var saveButton = AddButton(content, "Guardar configuración");
        saveButton.Click += (_, _) =>
        {
            SaveSettings();
            SetStatus("configuración guardada");
        };

        var startButton = AddButton(content, "Empezar a escuchar");
        startButton.Click += async (_, _) => await StartListeningAsync();

        var stopButton = AddButton(content, "Parar escucha");
        stopButton.Click += (_, _) =>
        {
            StopService(new Intent(this, typeof(BardoVoiceService)));
            SetStatus("detenido");
        };

        var testButton = AddButton(content, "Enviar comando de prueba");
        testButton.Click += async (_, _) => await SendTestCommandAsync();

        var diagnosticsButton = AddButton(content, "Diagnóstico de voz");
        diagnosticsButton.Click += (_, _) => ShowVoiceDiagnostics();

        SetContentView(root);
        RequestRequiredPermissions();
    }

    protected override void OnResume()
    {
        base.OnResume();

        if (BardoVoiceService.IsRunning)
        {
            SetStatus(BardoVoiceService.CurrentStatus);
        }
    }

    private async Task StartListeningAsync()
    {
        SaveSettings();

        if (CheckSelfPermission(Android.Manifest.Permission.RecordAudio) != Permission.Granted)
        {
            _startAfterPermission = true;
            RequestRequiredPermissions();
            SetStatus("esperando permiso de micrófono");
            return;
        }

        try
        {
            SetStatus("arrancando servicio de voz…");
            var intent = new Intent(this, typeof(BardoVoiceService));

            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                StartForegroundService(intent);
            }
            else
            {
                StartService(intent);
            }

            await Task.Delay(900);

            if (BardoVoiceService.IsRunning)
            {
                SetStatus(BardoVoiceService.CurrentStatus);
            }
            else
            {
                SetStatus("ERROR: Android no ha mantenido activo el servicio de voz");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"ERROR AL ARRANCAR: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ShowVoiceDiagnostics()
    {
        var microphoneGranted =
            CheckSelfPermission(Android.Manifest.Permission.RecordAudio) == Permission.Granted;
        var standardAvailable = SpeechRecognizer.IsRecognitionAvailable(this);
        var onDeviceAvailable =
            Build.VERSION.SdkInt >= BuildVersionCodes.S &&
            SpeechRecognizer.IsOnDeviceRecognitionAvailable(this);

        var running = BardoVoiceService.IsRunning;
        var serviceStatus = BardoVoiceService.CurrentStatus;
        var rms = float.IsNaN(BardoVoiceService.LastRmsDb)
            ? "sin señal"
            : $"{BardoVoiceService.LastRmsDb:0.0} dB";
        var recognizerEvent = BardoVoiceService.LastRecognizerEvent;

        SetStatus(
            $"Micro={microphoneGranted} · Voz sistema={standardAvailable} · Voz local={onDeviceAvailable} · Servicio={running} · RMS={rms} · Evento={recognizerEvent} · {serviceStatus}");
    }

    public override void OnRequestPermissionsResult(
        int requestCode,
        string[] permissions,
        Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

        if (requestCode != PermissionRequestCode || !_startAfterPermission)
        {
            return;
        }

        _startAfterPermission = false;

        if (CheckSelfPermission(Android.Manifest.Permission.RecordAudio) == Permission.Granted)
        {
            _ = StartListeningAsync();
        }
        else
        {
            SetStatus("permiso de micrófono denegado");
        }
    }

    private async Task SendTestCommandAsync()
    {
        SaveSettings();
        var settings = BardoSettingsStore.Load(this);
        var command = _testCommand?.Text?.Trim();

        if (string.IsNullOrWhiteSpace(command))
        {
            SetStatus("escribe un comando de prueba");
            return;
        }

        SetStatus($"enviando: {command}");
        var result = await new RelayCommandClient().SendAsync(settings, command);
        RunOnUiThread(() => SetStatus(result.Success ? $"OK: {result.Message}" : $"ERROR: {result.Message}"));
    }

    private void SaveSettings()
    {
        var defaults = BardoSettings.Default;
        var settings = new BardoSettings(
            ValueOrDefault(_relayUrl, defaults.RelayUrl),
            ValueOrDefault(_apiKey, defaults.ApiKey),
            ValueOrDefault(_deviceId, defaults.DeviceId),
            ValueOrDefault(_wakeWord, defaults.WakeWord));

        BardoSettingsStore.Save(this, settings);
    }

    private void RequestRequiredPermissions()
    {
        var permissions = new List<string>();

        if (CheckSelfPermission(Android.Manifest.Permission.RecordAudio) != Permission.Granted)
        {
            permissions.Add(Android.Manifest.Permission.RecordAudio);
        }

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu &&
            CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Permission.Granted)
        {
            permissions.Add(Android.Manifest.Permission.PostNotifications);
        }

        if (permissions.Count > 0)
        {
            RequestPermissions(permissions.ToArray(), PermissionRequestCode);
        }
    }

    private EditText AddField(LinearLayout parent, string label, string value)
    {
        var caption = new TextView(this)
        {
            Text = label,
            TextSize = 14f
        };
        caption.SetPadding(0, Dp(10), 0, Dp(4));
        parent.AddView(caption);

        var field = new EditText(this)
        {
            Text = value
        };
        field.SetSingleLine(true);
        parent.AddView(field, new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent,
            LinearLayout.LayoutParams.WrapContent));
        return field;
    }

    private Button AddButton(LinearLayout parent, string text)
    {
        var button = new Button(this)
        {
            Text = text
        };
        var parameters = new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent,
            LinearLayout.LayoutParams.WrapContent)
        {
            TopMargin = Dp(10)
        };
        parent.AddView(button, parameters);
        return button;
    }

    private void SetStatus(string text)
    {
        if (_status is not null)
        {
            _status.Text = $"Estado: {text}";
        }
    }

    private static string ValueOrDefault(EditText? field, string fallback)
    {
        var value = field?.Text?.Trim();
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private int Dp(int value) => (int)(value * Resources!.DisplayMetrics!.Density);
}
