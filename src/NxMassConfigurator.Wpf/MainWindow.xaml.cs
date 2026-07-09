using System.Text.Json;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using NxMassConfigurator.Models;
using NxMassConfigurator.Services;

namespace NxMassConfigurator.Wpf;

public partial class MainWindow
{
    private const string RepoOwner = "lucasdaniel2201";
    private const string RepoName = "nx-mass-configurator";

    private readonly MainViewModel _vm = new();
    private AutoUpdater? _autoUpdater;
    private CancellationTokenSource? _cts;
    private bool _updatePending;
    private bool _syncing;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        RefreshSummary();
        Loaded += async (_, _) => await CheckForUpdatesAsync();
        Closing += (_, _) => ApplyPendingUpdateIfNeeded();
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        var validation = ValidateConnectionInputs();
        if (validation != null)
        {
            MessageBox.Show(this, validation, "Verifique os dados", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetBusy(true, "Conectando ao NX e montando inventario...");
        LogTextBox.Clear();
        _vm.Results.Clear();
        _vm.Servers.Clear();
        _vm.VisibleDevices.Clear();
        RefreshSummary();

        var logger = new WpfLogger(Dispatcher, AppendLog);
        try
        {
            using var client = new NxClient(UrlTextBox.Text.Trim(), logger, InsecureSslCheckBox.IsChecked == true, ReadInt(TimeoutTextBox.Text, 15));
            logger.LogInfo($"Autenticando como '{UserTextBox.Text.Trim()}'...");
            await client.AuthenticateAsync(UserTextBox.Text.Trim(), PasswordBox.Password);

            logger.LogInfo("Buscando servidores...");
            var servers = await client.GetServersAsync();
            foreach (var server in servers.OrderBy(s => s.Name))
            {
                try
                {
                    logger.LogInfo($"Listando cameras de {server.Name}...");
                    var devices = await client.GetDevicesAsync(server.Id);
                    var serverItem = new ServerItem(server, devices);
                    HookSelectionUpdates(serverItem);
                    _vm.Servers.Add(serverItem);
                    logger.LogSucesso($"{server.Name}: {devices.Count} camera(s).");
                }
                catch (Exception ex)
                {
                    CrashLog.Write(ex, $"Falha ao listar cameras do servidor {server.Name} ({server.Id})");
                    logger.LogErro($"{server.Name}: falha ao listar cameras - {ex.Message}");
                }
            }

            await client.LogoutAsync();
            SetStatus($"Inventario carregado: {_vm.Servers.Count} servidor(es), {_vm.Servers.Sum(s => s.CameraCount)} camera(s).");
            logger.LogSucesso("Inventario pronto para selecao.");
        }
        catch (Exception ex)
        {
            logger.LogErro($"{ex.GetType().Name}: {ex.Message}");
            SetStatus("Falha ao carregar inventario.");
        }
        finally
        {
            SetBusy(false);
            RefreshVisibleDevices();
            RefreshSummary();
        }
    }

    private async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        var validation = ValidateRunInputs();
        if (validation != null)
        {
            MessageBox.Show(this, validation, "Verifique os dados", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetRunning(true);
        _vm.Results.Clear();
        _cts = new CancellationTokenSource();
        var logger = new WpfLogger(Dispatcher, AppendLog);
        var allResults = new List<OperationResult>();

        try
        {
            var targets = GetSelectedTargets();
            var fps = FpsCheckBox.IsChecked == true ? ReadNullableDouble(FpsTextBox.Text) : null;
            var quality = SelectedComboValue(QualityComboBox);
            var dryRun = DryRunCheckBox.IsChecked == true;

            using var client = new NxClient(UrlTextBox.Text.Trim(), logger, InsecureSslCheckBox.IsChecked == true, ReadInt(TimeoutTextBox.Text, 15));
            logger.LogInfo($"Autenticando como '{UserTextBox.Text.Trim()}' para executar...");
            await client.AuthenticateAsync(UserTextBox.Text.Trim(), PasswordBox.Password);

            var updater = new NxConfigUpdater(client, logger);
            var processor = new DeviceProcessor(updater, logger, ReadInt(ConcurrencyTextBox.Text, 6), 3, _cts.Token);

            foreach (var target in targets)
            {
                _cts.Token.ThrowIfCancellationRequested();
                logger.LogInfo($"Executando em {target.Server.Name}: {target.Devices.Count} camera(s) selecionada(s).");
                var results = await processor.ProcessAllAsync(
                    target.Devices.Select(d => d.Device).ToList(),
                    target.Server.Name,
                    fps,
                    quality,
                    StreamComboBox.SelectedIndex == 1,
                    StreamComboBox.SelectedIndex == 2,
                    AudioComboBox.SelectedIndex == 1,
                    AudioComboBox.SelectedIndex == 2,
                    SecondaryComboBox.SelectedIndex == 1,
                    SecondaryComboBox.SelectedIndex == 2,
                    EmptyToNull(CodecTextBox.Text),
                    EmptyToNull(ResolutionTextBox.Text),
                    SelectedComboValue(RtpComboBox),
                    MotionComboBox.SelectedIndex == 2,
                    MotionComboBox.SelectedIndex == 1,
                    dryRun);

                allResults.AddRange(results);
                AddResults(target.Server.Name, results);
            }

            if (!string.IsNullOrWhiteSpace(OutputTextBox.Text))
                new ReportWriter(logger).Write(allResults, OutputTextBox.Text.Trim(), $"{targets.Count} servidor(es)", fps, quality, dryRun);

            await client.LogoutAsync();
            logger.LogSucesso($"Concluido. Total: {allResults.Count}; sucesso: {allResults.Count(r => r.IsSuccess)}; falhas: {allResults.Count(r => r.IsFailed)}.");
            SetStatus("Execucao finalizada.");
        }
        catch (OperationCanceledException)
        {
            logger.LogWarn("Operacao cancelada pelo usuario.");
            SetStatus("Operacao cancelada.");
        }
        catch (Exception ex)
        {
            logger.LogErro($"{ex.GetType().Name}: {ex.Message}");
            SetStatus("Erro durante a execucao.");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetRunning(false);
            RefreshSummary();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
        => _cts?.Cancel();

    private async Task CheckForUpdatesAsync()
    {
        var logger = new WpfLogger(Dispatcher, AppendLog);
        _autoUpdater = new AutoUpdater(logger, RepoOwner, RepoName, "nx-mass-config-wpf");

        try
        {
            SetStatus("Verificando atualizacoes...");
            var downloaded = await _autoUpdater.CheckAndDownloadAsync();
            if (!downloaded)
            {
                SetStatus("App atualizado.");
                return;
            }

            _updatePending = true;
            UpdateBannerText.Text = "Uma nova versao foi baixada. Reinicie para aplicar a atualizacao.";
            UpdateBanner.Visibility = Visibility.Visible;
            SetStatus("Atualizacao pronta para aplicar.");
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "Falha na checagem de update da WPF");
            SetStatus("Nao foi possivel verificar atualizacoes.");
        }
    }

    private void ApplyUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyPendingUpdateIfNeeded();
        Application.Current.Shutdown();
    }

    private void DismissUpdateButton_Click(object sender, RoutedEventArgs e)
        => UpdateBanner.Visibility = Visibility.Collapsed;

    private void ApplyPendingUpdateIfNeeded()
    {
        if (!_updatePending || _autoUpdater == null)
            return;

        _autoUpdater.ApplyPendingUpdate();
        _updatePending = false;
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv|Todos os arquivos (*.*)|*.*", FileName = "resultado.csv" };
        if (dialog.ShowDialog(this) == true)
            OutputTextBox.Text = dialog.FileName;
    }

    private void ServerSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;

        if ((sender as CheckBox)?.DataContext is ServerItem server)
        {
            foreach (var device in server.Devices)
                device.IsSelected = server.IsSelected;
        }

        RefreshVisibleDevices();
        RefreshSummary();
    }

    private void SelectAllServers_Click(object sender, RoutedEventArgs e)
    {
        _syncing = true;
        foreach (var server in _vm.Servers)
        {
            server.IsSelected = true;
            foreach (var device in server.Devices)
                device.IsSelected = true;
        }
        _syncing = false;
        RefreshVisibleDevices();
        RefreshSummary();
    }

    private void ClearServers_Click(object sender, RoutedEventArgs e)
    {
        _syncing = true;
        foreach (var server in _vm.Servers)
        {
            server.IsSelected = false;
            foreach (var device in server.Devices)
                device.IsSelected = false;
        }
        _syncing = false;
        RefreshVisibleDevices();
        RefreshSummary();
    }

    private void SelectVisibleDevices_Click(object sender, RoutedEventArgs e)
    {
        foreach (var device in _vm.VisibleDevices)
            device.IsSelected = true;
        RefreshSummary();
    }

    private void ClearVisibleDevices_Click(object sender, RoutedEventArgs e)
    {
        foreach (var device in _vm.VisibleDevices)
            device.IsSelected = false;
        RefreshSummary();
    }

    private void FpsCheckBox_Changed(object sender, RoutedEventArgs e)
        => FpsTextBox.IsEnabled = FpsCheckBox.IsChecked == true;

    private void DryRunCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        ModeText.Text = DryRunCheckBox.IsChecked == true ? "Simulacao" : "Real";
    }

    private void RefreshVisibleDevices()
    {
        _vm.VisibleDevices.Clear();
        foreach (var device in _vm.Servers.Where(s => s.IsSelected).SelectMany(s => s.Devices))
            _vm.VisibleDevices.Add(device);
    }

    private void RefreshSummary()
    {
        var selected = _vm.Servers.SelectMany(s => s.Devices).Count(d => d.IsSelected);
        ServersCountText.Text = _vm.Servers.Count.ToString();
        DevicesCountText.Text = _vm.Servers.Sum(s => s.CameraCount).ToString();
        SelectedCountText.Text = selected.ToString();
        HeaderStatusText.Text = _vm.Servers.Count == 0 ? "Ambiente nao conectado" : $"{_vm.Servers.Count} servidor(es) carregado(s)";
        RunButton.IsEnabled = _cts == null && selected > 0;
        if (_cts == null)
            SetStatus(selected > 0 ? $"{selected} camera(s) selecionada(s)." : "Selecione servidores e cameras para habilitar a execucao.");
    }

    private void HookSelectionUpdates(ServerItem server)
    {
        server.PropertyChanged += SelectionItemChanged;
        foreach (var device in server.Devices)
            device.PropertyChanged += SelectionItemChanged;
    }

    private void SelectionItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncing || e.PropertyName != nameof(DeviceItem.IsSelected) && e.PropertyName != nameof(ServerItem.IsSelected))
            return;

        Dispatcher.BeginInvoke(RefreshSummary);
    }

    private void SetBusy(bool busy, string? status = null)
    {
        ConnectButton.IsEnabled = !busy;
        RunButton.IsEnabled = !busy && GetSelectedTargets().Count > 0;
        CancelButton.IsEnabled = false;
        Mouse.OverrideCursor = busy ? Cursors.Wait : null;
        if (status != null)
            SetStatus(status);
    }

    private void SetRunning(bool running)
    {
        ConnectButton.IsEnabled = !running;
        RunButton.IsEnabled = !running;
        CancelButton.IsEnabled = running;
        Mouse.OverrideCursor = running ? Cursors.Wait : null;
        SetStatus(running ? "Executando configuracoes nas cameras selecionadas..." : "Pronto.");
    }

    private void SetStatus(string message)
    {
        StatusTextBlock.Text = message;
        HeaderStatusText.Text = message;
    }

    private void AppendLog(string line)
    {
        LogTextBox.AppendText(line + Environment.NewLine);
        LogTextBox.ScrollToEnd();
    }

    private void AddResults(string serverName, IEnumerable<OperationResult> results)
    {
        foreach (var result in results)
        {
            _vm.Results.Add(new ResultItem
            {
                ServerName = serverName,
                DeviceName = result.DeviceName,
                OldFps = result.OldFps,
                NewFps = result.NewFps,
                Status = result.Status,
                ErrorMessage = result.ErrorMessage,
            });
        }
    }

    private List<RunTarget> GetSelectedTargets()
        => _vm.Servers
            .Select(server => new RunTarget(server, server.Devices.Where(d => d.IsSelected).ToList()))
            .Where(target => target.Devices.Count > 0)
            .ToList();

    private string? ValidateConnectionInputs()
    {
        if (string.IsNullOrWhiteSpace(UrlTextBox.Text)) return "Informe a URL do NX Witness.";
        if (string.IsNullOrWhiteSpace(UserTextBox.Text)) return "Informe o usuario.";
        if (string.IsNullOrWhiteSpace(PasswordBox.Password)) return "Informe a senha.";
        if (ReadInt(TimeoutTextBox.Text, 0) <= 0) return "Informe um timeout valido.";
        return null;
    }

    private string? ValidateRunInputs()
    {
        var connectionValidation = ValidateConnectionInputs();
        if (connectionValidation != null) return connectionValidation;
        if (_vm.Servers.Count == 0) return "Clique em Conectar e listar antes de executar.";
        if (GetSelectedTargets().Count == 0) return "Selecione ao menos uma camera.";
        if (FpsCheckBox.IsChecked == true && ReadNullableDouble(FpsTextBox.Text) == null) return "Informe um FPS valido.";
        if (ReadInt(ConcurrencyTextBox.Text, 0) <= 0) return "Informe uma concorrencia valida.";
        if (!HasAnyAction()) return "Selecione ao menos uma configuracao para aplicar.";
        return null;
    }

    private bool HasAnyAction()
        => FpsCheckBox.IsChecked == true ||
           QualityComboBox.SelectedIndex > 0 ||
           StreamComboBox.SelectedIndex > 0 ||
           AudioComboBox.SelectedIndex > 0 ||
           SecondaryComboBox.SelectedIndex > 0 ||
           !string.IsNullOrWhiteSpace(CodecTextBox.Text) ||
           !string.IsNullOrWhiteSpace(ResolutionTextBox.Text) ||
           RtpComboBox.SelectedIndex > 0 ||
           MotionComboBox.SelectedIndex > 0;

    private static int ReadInt(string value, int fallback)
        => int.TryParse(value, out var parsed) ? parsed : fallback;

    private static double? ReadNullableDouble(string value)
        => double.TryParse(value, out var parsed) ? parsed : null;

    private static string? EmptyToNull(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? SelectedComboValue(ComboBox combo)
        => combo.SelectedIndex <= 0 ? null : ((ComboBoxItem)combo.SelectedItem).Content.ToString();

    private sealed record RunTarget(ServerItem Server, List<DeviceItem> Devices);
}
