using System.Windows;
using AlexaPc.Agent.Services;
using AlexaPc.Agent.ViewModels;

namespace AlexaPc.Agent;

public partial class App : Application
{
    private RelayClientService? _relayClient;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var configurationService = new CommandConfigurationService();
        var relayConfigurationService = new RelayConfigurationService();
        var executionService = new CommandExecutionService();
        var dispatcher = new CommandDispatcher(configurationService, executionService);

        relayConfigurationService.Load();
        _relayClient = new RelayClientService(relayConfigurationService, dispatcher);

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
        window.Show();

        new DesktopShortcutService().EnsureShortcut();
        _relayClient.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_relayClient is not null)
        {
            _relayClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.OnExit(e);
    }
}
