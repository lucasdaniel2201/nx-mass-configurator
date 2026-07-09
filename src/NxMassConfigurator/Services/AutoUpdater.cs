using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using Octokit;

namespace NxMassConfigurator.Services;

/// <summary>
/// Auto-update via GitHub Releases (Octokit).
/// Modo silencioso: verifica em background, baixa e agenda substituição ao sair.
/// </summary>
public class AutoUpdater
{
    private readonly ILogger _logger;
    private readonly string _repoOwner;
    private readonly string _repoName;
    private readonly string _currentVersion;
    private readonly string _assetNameContains;

    private const string TokenEnvVar = "NX_GITHUB_TOKEN";

    // Token Fine-Grained embutido (read-only: apenas ler releases do repo)
    // Sobrescrito pela variável de ambiente NX_GITHUB_TOKEN se existir.
    private const string EmbeddedToken = "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";
    private static readonly SemVersion CurrentSemVer = ParseVersion(
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "1.4.0");

    // Caminho para o .exe baixado (pendente de aplicar)
    private string? _pendingUpdatePath;

    public AutoUpdater(ILogger logger, string repoOwner, string repoName, string assetNameContains = "nx-mass-config")
    {
        _logger = logger;
        _repoOwner = repoOwner;
        _repoName = repoName;
        _assetNameContains = assetNameContains;
        _currentVersion = CurrentSemVer.ToString();
    }

    /// <summary>
    /// Verifica se há update disponível no GitHub e, se houver, baixa silenciosamente.
    /// Retorna true se um update foi baixado e está pendente (será aplicado ao sair do processo).
    /// </summary>
    public async Task<bool> CheckAndDownloadAsync()
    {
        try
        {
            var token = Environment.GetEnvironmentVariable(TokenEnvVar);
            var client = CreateClient(token);

            _logger.LogInfo("Verificando atualizações no GitHub...");

            var releases = await client.Repository.Release.GetAll(_repoOwner, _repoName);
            var latest = releases.FirstOrDefault(r => !r.Prerelease);
            if (latest == null)
            {
                _logger.LogWarn("Nenhum release estável encontrado no GitHub.");
                return false;
            }

            var remoteVersion = ParseVersion(latest.TagName?.TrimStart('v') ?? "0.0.0");

            if (remoteVersion <= CurrentSemVer)
            {
                _logger.LogInfo($"Versão {_currentVersion} é a mais recente.");
                return false;
            }

            _logger.LogSucesso($"Nova versão disponível: {remoteVersion} (atual: {_currentVersion})");

            // Encontra o asset .exe
            var exeAsset = latest.Assets.FirstOrDefault(a =>
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                a.Name.Contains(_assetNameContains, StringComparison.OrdinalIgnoreCase));

            if (exeAsset == null)
            {
                _logger.LogWarn($"Asset .exe contendo '{_assetNameContains}' não encontrado no release.");
                return false;
            }

            // Download
            var tempDir = Path.Combine(Path.GetTempPath(), "nx-mass-config-update");
            Directory.CreateDirectory(tempDir);
            var downloadPath = Path.Combine(tempDir, Path.GetFileName(Environment.ProcessPath ?? exeAsset.Name));

            _logger.LogInfo($"Baixando {exeAsset.Name} ({exeAsset.Size / 1024} KB)...");

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("nx-mass-config");
            if (!string.IsNullOrEmpty(token))
                httpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var response = await httpClient.GetAsync(exeAsset.BrowserDownloadUrl);
            response.EnsureSuccessStatusCode();

            await using var fs = System.IO.File.Create(downloadPath);
            await response.Content.CopyToAsync(fs);

            _logger.LogSucesso($"Download concluído: {downloadPath}");
            _pendingUpdatePath = downloadPath;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarn($"Falha ao verificar updates: {ex.Message}. Continuando sem update.");
            return false;
        }
    }

    /// <summary>
    /// Aplica o update pendente: cria .bat que substitui o .exe ao sair do processo.
    /// Chame ao final do programa (após logout, antes de encerrar).
    /// </summary>
    public void ApplyPendingUpdate()
    {
        if (_pendingUpdatePath == null || !File.Exists(_pendingUpdatePath))
            return;

        try
        {
            var currentExe = Environment.ProcessPath;
            if (currentExe == null) return;
            var currentPid = Environment.ProcessId;

            var batPath = Path.Combine(Path.GetTempPath(), "nx-mass-config-update.bat");
            var batContent = $"""
                @echo off
                echo Aguardando encerramento do nx-mass-config...
                :loop
                timeout /t 1 /nobreak >nul 2>&1
                tasklist /FI "PID eq {currentPid}" | find "{currentPid}" >nul 2>&1
                if not errorlevel 1 goto loop

                echo Substituindo {currentExe}...
                move /Y "{_pendingUpdatePath}" "{currentExe}"

                echo Atualizacao concluida. Iniciando nova versao...
                start "" "{currentExe}"
                del "%~f0"
                """;

            File.WriteAllText(batPath, batContent);

            // Spawn detached batch file
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{batPath}\"",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);

            _logger.LogSucesso("Update será aplicado ao sair. O programa será reiniciado automaticamente.");
        }
        catch (Exception ex)
        {
            _logger.LogErro($"Falha ao aplicar update: {ex.Message}");
        }
    }

    /// <summary>
    /// Exibe status do auto-update (usado pela flag --check-update).
    /// </summary>
    public async Task CheckAndReportAsync()
    {
        var token = Environment.GetEnvironmentVariable(TokenEnvVar);
        var client = CreateClient(token);

        Console.WriteLine($"Versão local : {_currentVersion}");

        try
        {
            var releases = await client.Repository.Release.GetAll(_repoOwner, _repoName);
            var latest = releases.FirstOrDefault(r => !r.Prerelease);
            if (latest != null)
            {
                var remote = ParseVersion(latest.TagName?.TrimStart('v') ?? "0.0.0");
                Console.WriteLine($"Último release: {remote}");
                Console.WriteLine($"URL: {latest.HtmlUrl}");
                if (remote > CurrentSemVer)
                    Console.WriteLine("Status: UPDATE DISPONÍVEL");
                else
                    Console.WriteLine("Status: atualizado");
            }
            else
            {
                Console.WriteLine("Nenhum release encontrado.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erro ao consultar GitHub: {ex.Message}");
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private static GitHubClient CreateClient(string? token)
    {
        // Prioridade: variável de ambiente > token embutido
        var effectiveToken = token ?? EmbeddedToken;
        var client = new GitHubClient(new ProductHeaderValue("nx-mass-config"));
        if (!string.IsNullOrEmpty(effectiveToken))
            client.Credentials = new Credentials(effectiveToken);
        return client;
    }

    private static SemVersion ParseVersion(string version)
    {
        var match = Regex.Match(version, @"(\d+)\.(\d+)\.(\d+)");
        if (match.Success)
        {
            return new SemVersion(
                int.Parse(match.Groups[1].Value),
                int.Parse(match.Groups[2].Value),
                int.Parse(match.Groups[3].Value));
        }
        return new SemVersion(0, 0, 0);
    }
}

/// <summary>
/// SemVer simples (Major.Minor.Patch).
/// </summary>
public readonly record struct SemVersion(int Major, int Minor, int Patch)
    : IComparable<SemVersion>
{
    public int CompareTo(SemVersion other)
    {
        int c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        return Patch.CompareTo(other.Patch);
    }

    public static bool operator <(SemVersion a, SemVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(SemVersion a, SemVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemVersion a, SemVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemVersion a, SemVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}
