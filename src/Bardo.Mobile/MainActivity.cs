using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using Bardo.Mobile.Infrastructure;

namespace Bardo.Mobile;

[Activity(
    Label = "Bardo",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    ScreenOrientation = ScreenOrientation.Portrait)]
[IntentFilter(
    [Intent.ActionMain],
    Categories = [Intent.CategoryHome, Intent.CategoryDefault])]
public sealed class MainActivity : Activity
{
    private const int PermissionRequestCode = 1001;

    private EditText? _relayUrl;
    private EditText? _apiKey;
    private EditText? _deviceId;
    private EditText? _wakeWord;
    private EditText? _pcMacAddress;
    private EditText? _testCommand;
    private TextView? _status;
    private TextView? _dedicatedStatus;
    private bool _startAfterPermission;
    private bool _autoStartAttempted;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        DedicatedModeController.ApplyWindow(this);

        var settings = BardoSettingsStore.Load(this);
        var root = new ScrollView(this);
        var content = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical
        };

        content.SetPadding(Dp(20), Dp(28), Dp(20), Dp(28));
        root.AddView(content);

        var logo = new ImageView(this);
        logo.SetImageResource(Resource.Drawable.bardo_app_icon);
        logo.SetAdjustViewBounds(true);
        content.AddView(logo, new LinearLayout.LayoutParams(Dp(112), Dp(112))
        {
            Gravity = GravityFlags.CenterHorizontal
        });

        var title = new TextView(this)
        {
            Text = "BARDO",
            TextSize = 30f,
            Gravity = GravityFlags.CenterHorizontal
        };
        content.AddView(title);

        var subtitle = new TextView(this)
        {
            Text = "Control de voz local para PC y dispositivos",
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

        _dedicatedStatus = new TextView(this)
        {
            Text = DedicatedModeController.GetStatus(this),
            TextSize = 14f
        };
        _dedicatedStatus.SetPadding(0, 0, 0, Dp(12));
        content.AddView(_dedicatedStatus);

        _relayUrl = AddField(content, "Relay del PC", settings.RelayUrl);
        _apiKey = AddField(content, "API key", settings.ApiKey);
        _deviceId = AddField(content, "Device ID", settings.DeviceId);
        _wakeWord = AddField(content, "Palabra de activación", settings.WakeWord);
        _pcMacAddress = AddField(content, "MAC del PC · Wake-on-LAN", settings.PcMacAddress);
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
        _startAfterPermission = true;
        RequestRequiredPermissions();
    }

    protected override void OnStart()
    {
        base.OnStart();
        BardoVoiceService.StatusChanged += OnVoiceStatusChanged;
    }

    protected override void OnStop()
    {
        BardoVoiceService.StatusChanged -= OnVoiceStatusChanged;
        base.OnStop();
    }

    protected override void OnResume()
    {
        base.OnResume();
        DedicatedModeController.ApplyWindow(this);
        DedicatedModeController.ApplyDeviceOwnerPolicies(this);
        UpdateDedicatedStatus();
        RefreshLearnedPcMac();

        if (BardoVoiceService.IsRunning)
        {
            SetStatus(BardoVoiceService.CurrentStatus);
        }
        else if (!_autoStartAttempted &&
                 CheckSelfPermission(Android.Manifest.Permission.RecordAudio) == Permission.Granted)
        {
            _autoStartAttempted = true;
            _startAfterPermission = false;
            _ = StartListeningAsync();
        }
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus)
        {
            DedicatedModeController.ApplyWindow(this);
        }
    }

    public override void OnBackPressed()
    {
        // Bardo es el terminal principal del dispositivo. El mantenimiento se hace
        // desde sus propios controles o mediante ADB, no abandonando la aplicación.
    }

    private void OnVoiceStatusChanged(string status)
    {
        RunOnUiThread(() =>
        {
            SetStatus(status);
            RefreshLearnedPcMac();
        });
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
        bool microphoneGranted =
            CheckSelfPermission(Android.Manifest.Permission.RecordAudio) == Permission.Granted;
        bool recognizerAvailable = AndroidSpeechEngine.IsAvailable(this);
        bool running = BardoVoiceService.IsRunning;
        bool engineReady = BardoVoiceService.LocalEngineReady;
        string serviceStatus = BardoVoiceService.CurrentStatus;
        string rms = float.IsNaN(BardoVoiceService.LastRmsDb)
            ? "sin señal"
            : $"{BardoVoiceService.LastRmsDb:0.0} dB";
        string recognizerEvent = BardoVoiceService.LastRecognizerEvent;
        string dedicatedMode = DedicatedModeController.GetStatus(this);
        string pcMac = BardoSettingsStore.Load(this).PcMacAddress;

        SetStatus(
            $"Micro={microphoneGranted} · VozAndroid={recognizerAvailable} · MotorLocal={engineReady} · Servicio={running} · RMS={rms} · MAC-PC={(string.IsNullOrWhiteSpace(pcMac) ? "pendiente" : pcMac)} · Evento={recognizerEvent} · {dedicatedMode} · {serviceStatus}");
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
            _autoStartAttempted = true;
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
            ValueOrDefault(_wakeWord, defaults.WakeWord),
            _pcMacAddress?.Text?.Trim() ?? defaults.PcMacAddress);

        BardoSettingsStore.Save(this, settings);
    }

    private void RefreshLearnedPcMac()
    {
        if (_pcMacAddress is null || !string.IsNullOrWhiteSpace(_pcMacAddress.Text))
        {
            return;
        }

        string learnedMac = BardoSettingsStore.Load(this).PcMacAddress;
        if (!string.IsNullOrWhiteSpace(learnedMac))
        {
            _pcMacAddress.Text = learnedMac;
        }
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

    private void UpdateDedicatedStatus()
    {
        if (_dedicatedStatus is not null)
        {
            _dedicatedStatus.Text = DedicatedModeController.GetStatus(this);
        }
    }

    private static string ValueOrDefault(EditText? field, string fallback)
    {
        var value = field?.Text?.Trim();
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private int Dp(int value) => (int)(value * Resources!.DisplayMetrics!.Density);
}
