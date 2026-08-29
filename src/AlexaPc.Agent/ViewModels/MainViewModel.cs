using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using AlexaPc.Agent.Models;
using AlexaPc.Agent.Mvvm;
using AlexaPc.Agent.Services;

namespace AlexaPc.Agent.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly CommandConfigurationService _configurationService;
    private readonly CommandDispatcher _dispatcher;
    private readonly AsyncRelayCommand _executeSelectedCommand;
    private CommandDefinition? _selectedCommand;
    private string _status = "Preparado.";

    public MainViewModel(
        CommandConfigurationService configurationService,
        CommandDispatcher dispatcher)
    {
        _configurationService = configurationService;
        _dispatcher = dispatcher;

        _executeSelectedCommand = new AsyncRelayCommand(ExecuteSelectedAsync, () => SelectedCommand is not null);
        ReloadCommand = new RelayCommand(Reload);
        OpenConfigurationCommand = new RelayCommand(OpenConfiguration);

        Reload();
    }

    public ObservableCollection<CommandDefinition> Commands { get; } = [];

    public string ConfigurationPath => _configurationService.ConfigurationPath;

    public CommandDefinition? SelectedCommand
    {
        get => _selectedCommand;
        set
        {
            if (SetProperty(ref _selectedCommand, value))
            {
                _executeSelectedCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public ICommand ExecuteSelectedCommand => _executeSelectedCommand;
    public ICommand ReloadCommand { get; }
    public ICommand OpenConfigurationCommand { get; }

    private void Reload()
    {
        try
        {
            var commands = _configurationService.Load();
            Commands.Clear();

            foreach (var command in commands)
            {
                Commands.Add(command);
            }

            Status = $"{Commands.Count} comandos cargados.";
        }
        catch (Exception ex)
        {
            Status = $"Error al cargar configuración: {ex.Message}";
        }
    }

    private async Task ExecuteSelectedAsync()
    {
        if (SelectedCommand is null)
        {
            return;
        }

        Status = $"Ejecutando: {SelectedCommand.Name}…";
        var result = await _dispatcher.ExecuteAsync(SelectedCommand);
        Status = result.Message;
    }

    private void OpenConfiguration()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = ConfigurationPath,
                UseShellExecute = true
            });
            Status = "commands.json abierto. Guarda los cambios y pulsa Recargar.";
        }
        catch (Exception ex)
        {
            Status = $"No se pudo abrir commands.json: {ex.Message}";
        }
    }
}
