using System.Windows;
using AlexaPc.Agent.Services;
using AlexaPc.Agent.ViewModels;

namespace AlexaPc.Agent;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var configurationService = new CommandConfigurationService();
        var executionService = new CommandExecutionService();
        var dispatcher = new CommandDispatcher(configurationService, executionService);
        var viewModel = new MainViewModel(configurationService, dispatcher);

        var window = new MainWindow
        {
            DataContext = viewModel
        };

        MainWindow = window;
        window.Show();
    }
}
