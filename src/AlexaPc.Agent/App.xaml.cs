using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using AlexaPc.Agent.Services;
using AlexaPc.Agent.ViewModels;

namespace AlexaPc.Agent;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = "Local\\AlexaPc.Agent.SingleInstance";

    private RelayClientService? _relayClient;
    private TrayIconService? _trayIcon;
    private AppLogService? _log;
    private Mutex? _singleInstanceMutex;
    private bool _isExiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        _log = new AppLogService();
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            ActivateExistingWindow();
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);
        _log.Info("application_started", new { background = e.Args.Contains("--background") });

        DispatcherUnhandledException += (_, args) =>
        {
            _log.Error("dispatcher_unhandled_exception", args.Exception);
            args.Handled = true;
        };

        var configurationService = new CommandConfigurationService();
        var relayConfigurationService = new RelayConfigurationService();
        var assistantConfigurationService = new AssistantConfigurationService();
        var executionService = new CommandExecutionService(_log);
        var llamaService = new LocalLlamaService(assistantConfigurationService, _log);
        var assistantService = new LocalAssistantService(
            configurationService,
            executionService,
            llamaService,
            _log);
        var dispatcher = new CommandDispatcher(configurationService, executionService, assistantService, _log);

        relayConfigurationService.Load();
        assistantConfigurationService.Load();
        _relayClient = new RelayClientService(relayConfigurationService, dispatcher, _log);

        var viewModel = new MainViewModel(
            configurationService,
            relayConfigurationService,
            dispatcher,
            _relayClient);

        var window = new MainWindow
        {
            DataContext = viewModel
        };

        MainWindow = window;
        _trayIcon = new TrayIconService(window, ExitApplication);
        window.Closing += (_, args) =>
        {
            if (_isExiting)
            {
                return;
            }

            args.Cancel = true;
            _trayIcon.HideWindow(showNotice: true);
        };

        if (e.Args.Contains("--background"))
        {
            _trayIcon.HideWindow(showNotice: false);
        }
        else
        {
            window.Show();
        }

        new DesktopShortcutService().EnsureShortcuts();
        _relayClient.Start();
        _ = llamaService.WarmUpAsync();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _isExiting = true;
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _isExiting = true;

        if (_relayClient is not null)
        {
            _relayClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _trayIcon?.Dispose();
        _log?.Info("application_stopped");

        if (_singleInstanceMutex is not null)
        {
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        base.OnExit(e);
    }

    private void ExitApplication()
    {
        _isExiting = true;
        Shutdown();
    }

    private static void ActivateExistingWindow()
    {
        var handle = FindWindow(null, "AlexaPc");
        if (handle == IntPtr.Zero)
        {
            return;
        }

        ShowWindow(handle, 9);
        SetForegroundWindow(handle);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);
}
