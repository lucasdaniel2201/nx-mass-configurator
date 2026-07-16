using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using NxMassConfigurator.Models.Qnap;
using NxMassConfigurator.Services;
using NxMassConfigurator.Services.Config;
using NxMassConfigurator.Services.Qnap;
using NxMassConfigurator.Services.Security;
using NxMassConfigurator.Services.Smb;

namespace NxMassConfigurator.Wpf.ViewModels;

/// <summary>
/// ViewModel for the "Remover por MAC" tab.
/// </summary>
public sealed class MacFinderViewModel : INotifyPropertyChanged
{
    private readonly SynchronizationContext? _syncContext = SynchronizationContext.Current;

    public event PropertyChangedEventHandler? PropertyChanged;

    // ── NAS Configuration ────────────────────────────────────────────
    public ObservableCollection<NasServerItem> Servers { get; } = new();
    public ObservableCollection<string> Subfolders { get; } = new() { "hi_quality", "low_quality" };

    private string _share = "Public";
    public string Share
    {
        get => _share;
        set { _share = value; OnPropertyChanged(); }
    }

    private string _username = "";
    public string Username
    {
        get => _username;
        set { _username = value; OnPropertyChanged(); }
    }

    private string _password = "";
    public string Password
    {
        get => _password;
        set { _password = value; OnPropertyChanged(); }
    }

    private bool _tlsVerify = false;
    public bool TlsVerify
    {
        get => _tlsVerify;
        set { _tlsVerify = value; OnPropertyChanged(); }
    }

    // ── Search ────────────────────────────────────────────────────────
    private string _macAddress = "";
    public string MacAddress
    {
        get => _macAddress;
        set
        {
            _macAddress = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NormalizedMac));
        }
    }

    public string NormalizedMac => MacAddressHelper.Normalize(MacAddress);

    private bool _isSearching;
    public bool IsSearching
    {
        get => _isSearching;
        set { _isSearching = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanSearch)); }
    }

    public bool CanSearch => !IsSearching && !string.IsNullOrWhiteSpace(MacAddress);

    // ── Results ──────────────────────────────────────────────────────
    public ObservableCollection<MacFoundPath> FoundPaths { get; } = new();

    private double _lastElapsed;
    public double LastElapsed
    {
        get => _lastElapsed;
        set { _lastElapsed = value; OnPropertyChanged(); OnPropertyChanged(nameof(ElapsedText)); }
    }

    public string? ElapsedText => LastElapsed > 0 ? $"{LastElapsed:F1}s" : null;

    public int FoundCount => FoundPaths.Count;

    // ── Progress ─────────────────────────────────────────────────────
    private string _statusText = "Pronto. Configure os servidores e busque um MAC.";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    private double _searchProgress;
    public double SearchProgress
    {
        get => _searchProgress;
        set { _searchProgress = value; OnPropertyChanged(); }
    }

    private string _searchProgressText = "";
    public string SearchProgressText
    {
        get => _searchProgressText;
        set { _searchProgressText = value; OnPropertyChanged(); }
    }

    private bool _isDeleting;
    public bool IsDeleting
    {
        get => _isDeleting;
        set { _isDeleting = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanDelete)); }
    }

    public bool CanDelete => !IsDeleting && !IsSearching && FoundPaths.Count > 0;

    // ── Log ────────────────────────────────────────────────────────────
    private string _logText = "";
    public string LogText
    {
        get => _logText;
        set { _logText = value; OnPropertyChanged(); }
    }

    private CancellationTokenSource? _cts;

    // ── Initialization ─────────────────────────────────────────────────

    /// <summary>
    /// Load settings from disk and populate the ViewModel.
    /// </summary>
    public void LoadSettings()
    {
        var settings = NasSettingsStore.Load();
        Share = settings.QnapShare;
        Username = settings.QnapUsername;
        Password = NasSettingsStore.UnprotectPassword(settings.QnapPasswordProtected) ?? "";
        TlsVerify = settings.QnapTlsVerify;

        Subfolders.Clear();
        foreach (var sf in settings.Subfolders)
            Subfolders.Add(sf);

        Servers.Clear();
        foreach (var server in settings.Servers)
        {
            var item = new NasServerItem(server);
            item.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CanSearch));
            Servers.Add(item);
        }
    }

    /// <summary>
    /// Save current settings to disk.
    /// </summary>
    public void SaveSettings()
    {
        var settings = new NasSettings
        {
            QnapShare = Share,
            QnapUsername = Username,
            QnapPasswordProtected = NasSettingsStore.ProtectPassword(Password),
            QnapTlsVerify = TlsVerify,
            Subfolders = Subfolders.ToList(),
            Servers = Servers.Select(s => s.Config).ToList()
        };
        NasSettingsStore.Save(settings);
        AppendLog("[OK] Configurações salvas.");
    }

    // ── Search ────────────────────────────────────────────────────────

    public async Task SearchAsync()
    {
        if (IsSearching || string.IsNullOrWhiteSpace(MacAddress))
            return;

        var targetMac = NormalizedMac;
        if (targetMac.Length < 17) // Minimum valid: XX-XX-XX-XX-XX-XX
        {
            StatusText = "MAC Address inválido.";
            return;
        }

        SetSearching(true);
        FoundPaths.Clear();
        LogText = "";

        var startTime = DateTime.Now;
        _cts = new CancellationTokenSource();

        try
        {
            var selectedServers = Servers.Where(s => s.IsSelected).ToList();
            var subfolderList = Subfolders.ToList();

            AppendLog($"[INFO] Buscando MAC {targetMac} em {selectedServers.Count} servidor(es)...");
            StatusText = "Buscando pastas...";

            var sem = new SemaphoreSlim(4);
            var lockObj = new object();
            var completedServers = 0;
            var totalServers = selectedServers.Count * subfolderList.Count;

            var tasks = selectedServers.Select(async server =>
            {
                await sem.WaitAsync(_cts.Token);
                try
                {
                    foreach (var subfolder in subfolderList)
                    {
                        _cts.Token.ThrowIfCancellationRequested();

                        var label = $"\\\\{server.Config.Ip}\\{server.Config.Share}\\*\\{subfolder}";
                        AppendLog($"[INFO] Verificando {label}...");

                        try
                        {
                            // Use global credentials as fallback when server-specific ones are empty
                            var effectiveUser = string.IsNullOrWhiteSpace(server.Config.Username)
                                ? Username
                                : server.Config.Username;

                            var (paths, method) = await SmartQnapSearch.SearchAsync(
                                server.Config.Ip,
                                server.Config.Share,
                                effectiveUser,
                                Password,
                                targetMac,
                                subfolder,
                                tlsVerify: TlsVerify,
                                progress: null,
                                logProgress: new Progress<string>(msg => AppendLog(msg)),
                                ct: _cts.Token);

                            lock (lockObj)
                            {
                                foreach (var path in paths)
                                    FoundPaths.Add(path);
                            }

                            if (paths.Count > 0)
                                AppendLog($"[ OK ] {server.Config.Ip}/{subfolder}: {paths.Count} pasta(s) via {method}.");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            AppendLog($"[ERRO] {server.Config.Ip}/{subfolder}: {ex.Message}");
                        }

                        Interlocked.Increment(ref completedServers);
                        SearchProgress = (double)completedServers / totalServers;
                        SearchProgressText = $"{completedServers}/{totalServers}";
                    }
                }
                finally
                {
                    sem.Release();
                }
            });

            await Task.WhenAll(tasks);

            var elapsed = (DateTime.Now - startTime).TotalSeconds;
            LastElapsed = elapsed;

            if (FoundPaths.Count == 0)
            {
                AppendLog($"[WARN] Nenhuma pasta encontrada para o MAC {targetMac}.");
                StatusText = $"Busca concluída em {elapsed:F1}s — nenhuma pasta encontrada.";
            }
            else
            {
                AppendLog($"[ OK ] Total: {FoundPaths.Count} pasta(s) em {elapsed:F1}s.");
                StatusText = $"Busca concluída: {FoundPaths.Count} pasta(s) em {elapsed:F1}s.";
            }

            OnPropertyChanged(nameof(FoundCount));
        }
        catch (OperationCanceledException)
        {
            AppendLog("[WARN] Busca cancelada pelo usuário.");
            StatusText = "Busca cancelada.";
        }
        catch (Exception ex)
        {
            AppendLog($"[ERRO] {ex.Message}");
            StatusText = $"Erro na busca: {ex.Message}";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetSearching(false);
            OnPropertyChanged(nameof(FoundCount));
        }
    }

    public void CancelSearch()
    {
        _cts?.Cancel();
    }

    // ── Delete ────────────────────────────────────────────────────────

    public async Task DeleteSingleAsync(string serverIp, string uncPath, IProgress<(double, string)>? uiProgress = null)
    {
        var path = PathSecurityValidator.UncToRelativeTarget(uncPath, serverIp, Share);
        await SmartQnapDelete.DeleteAsync(serverIp, Share, Username, Password, path);
    }

    public async Task DeleteAllAsync(
        IProgress<(double progress, string text)>? progress = null,
        IProgress<string>? fileStatus = null)
    {
        if (IsDeleting || FoundPaths.Count == 0)
            return;

        SetDeleting(true);
        _cts = new CancellationTokenSource();

        try
        {
            var paths = FoundPaths.ToList();
            var okCount = 0;
            var failCount = 0;
            var failedPaths = new List<string>();

            for (var i = 0; i < paths.Count; i++)
            {
                _cts.Token.ThrowIfCancellationRequested();

                var path = paths[i];
                var folderLabel = path.UncPath.Split('\\').Last();

                progress?.Report(((double)i / paths.Count, $"Pasta {i + 1}/{paths.Count} ({path.ServerIp}): {folderLabel}"));

                try
                {
                    var rel = PathSecurityValidator.UncToRelativeTarget(path.UncPath, path.ServerIp, path.Share);
                    var method = await SmartQnapDelete.DeleteAsync(
                        path.ServerIp,
                        path.Share,
                        Username,
                        Password,
                        rel,
                        progress: new Progress<DeleteProgress>(delProgress =>
                        {
                            fileStatus?.Report($"[{delProgress.CurrentIp}] {delProgress.CurrentFile}");
                            progress?.Report((
                                (i + (double)delProgress.DeletedCount / Math.Max(delProgress.TotalItems, 1)) / paths.Count,
                                $"Pasta {i + 1}/{paths.Count} ({delProgress.CurrentIp}): {delProgress.DeletedCount}/{delProgress.TotalItems} itens"));
                        }),
                        ct: _cts.Token);

                    okCount++;
                    AppendLog($"[ OK ] Excluída: {path.UncPath} (via {method})");
                }
                catch (Exception ex)
                {
                    failCount++;
                    failedPaths.Add($"{path.ServerIp}/{folderLabel}: {ex.Message}");
                    AppendLog($"[ERRO] Falha ao excluir {path.UncPath}: {ex.Message}");
                }
            }

            // Refresh the list
            var remaining = FoundPaths.Except(paths.Take(okCount)).ToList();
            FoundPaths.Clear();
            foreach (var r in remaining)
                FoundPaths.Add(r);

            OnPropertyChanged(nameof(FoundCount));

            if (failCount == 0)
            {
                AppendLog($"[ OK ] Todas as {okCount} pastas excluídas com sucesso.");
                StatusText = $"{okCount} pasta(s) excluída(s).";
            }
            else
            {
                AppendLog($"[WARN] {okCount} excluídas, {failCount} falharam.");
                StatusText = $"{okCount} ok, {failCount} falhas.";
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("[WARN] Exclusão cancelada.");
            StatusText = "Exclusão cancelada.";
        }
        catch (Exception ex)
        {
            AppendLog($"[ERRO] {ex.Message}");
            StatusText = $"Erro: {ex.Message}";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetDeleting(false);
        }
    }

    public void CancelDelete()
    {
        _cts?.Cancel();
    }

    // ── Servers management ────────────────────────────────────────────

    public void AddServer(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return;
        Servers.Add(new NasServerItem(new NasServerConfig
        {
            Ip = ip.Trim(),
            Share = Share,
            Selected = true
        }));
    }

    public void RemoveServer(NasServerItem item)
    {
        Servers.Remove(item);
    }

    // ── Logging ───────────────────────────────────────────────────────

    public void AppendLog(string message)
    {
        var timestamped = $"[{DateTime.Now:HH:mm:ss}] {message}";
        LogText += timestamped + Environment.NewLine;
    }

    // ── Internal ──────────────────────────────────────────────────────

    private void SetSearching(bool searching)
    {
        IsSearching = searching;
        OnPropertyChanged(nameof(CanSearch));
        OnPropertyChanged(nameof(CanDelete));
        SearchProgress = 0;
        SearchProgressText = searching ? "Iniciando..." : "";
    }

    private void SetDeleting(bool deleting)
    {
        IsDeleting = deleting;
        OnPropertyChanged(nameof(CanDelete));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// Wraps a NasServerConfig for WPF data binding with IsSelected support.
/// </summary>
public sealed class NasServerItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public NasServerItem(NasServerConfig config)
    {
        Config = config;
        _isSelected = config.Selected;
    }

    public NasServerConfig Config { get; }
    public string Ip => Config.Ip;
    public string Share => Config.Share;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            Config.Selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
