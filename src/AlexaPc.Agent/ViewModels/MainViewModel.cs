using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Windows.Input;
using AlexaPc.Agent.Models;
using AlexaPc.Agent.Mvvm;
using AlexaPc.Agent.Services;

namespace AlexaPc.Agent.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly CommandConfigurationService _configurationService;
    private readonly RelayConfigurationService _relayConfigurationService;
    private readonly CommandDispatcher _dispatcher;
    private readonly RelayClientService _relayClient;
    private readonly AsyncRelayCommand _executeSelectedCommand;
    private readonly SynchronizationContext? _uiContext;
    private CommandDefinition? _selectedCommand;
    private string _status = "Preparado.";
    private string _relayStatus = "RELAY · INICIANDO";
    private bool _isRelayConnected;

    public MainViewModel(
        CommandConfigurationService configurationService,
        RelayConfigurationService relayConfigurationService,
        CommandDispatcher dispatcher,
        RelayClientService relayClient)
    {
        _configurationService = configurationService;
        _relayConfigurationService = relayConfigurationService;
        _dispatcher = dispatcher;
        _relayClient = relayClient;
        _uiContext = SynchronizationContext.Current;

        _executeSelectedCommand = new AsyncRelayCommand(ExecuteSelectedAsync, () => SelectedCommand is not null);
        ReloadCommand = new RelayCommand(Reload);
        OpenConfigurationCommand = new RelayCommand(OpenConfiguration);
        OpenRelayConfigurationCommand = new RelayCommand(OpenRelayConfiguration);

        _relayClient.ConnectionStateChanged += RelayClientOnConnectionStateChanged;
        Reload();
    }

    public ObservableCollection<CommandDefinition> Commands { get; } = [];

    public string ConfigurationPath => _configurationService.ConfigurationPath;
    public string RelayConfigurationPath => _relayConfigurationService.ConfigurationPath;

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

    public string RelayStatus
    {
        get => _relayStatus;
        private set => SetProperty(ref _relayStatus, value);
    }

    public bool IsRelayConnected
    {
        get => _isRelayConnected;
        private set => SetProperty(ref _isRelayConnected, value);
    }

    public ICommand ExecuteSelectedCommand => _executeSelectedCommand;
    public ICommand ReloadCommand { get; }
    public ICommand OpenConfigurationCommand { get; }
    public ICommand OpenRelayConfigurationCommand { get; }

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
        => OpenFile(ConfigurationPath, "commands.json abierto. Guarda los cambios y pulsa Recargar.");

    private void OpenRelayConfiguration()
        => OpenFile(RelayConfigurationPath, "relay.json abierto. Reinicia AlexaPc después de cambiar la conexión.");

    private void OpenFile(string path, string successMessage)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
            Status = successMessage;
        }
        catch (Exception ex)
        {
            Status = $"No se pudo abrir la configuración: {ex.Message}";
        }
    }

    private void RelayClientOnConnectionStateChanged(object? sender, RelayConnectionStateChangedEventArgs e)
    {
        void Apply()
        {
            RelayStatus = e.Label;
            IsRelayConnected = e.IsConnected;
        }

        if (_uiContext is null)
        {
            Apply();
            return;
        }

        _uiContext.Post(_ => Apply(), null);
    }
}
