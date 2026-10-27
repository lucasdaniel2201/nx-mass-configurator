using System.Collections.Concurrent;
using System.Text.Json;
using NxMassConfigurator.Models;

namespace NxMassConfigurator.Services;

/// <summary>
/// Serviço de backup das configurações atuais das câmeras via GET.
/// Grava o JSON bruto de cada device e um manifesto do backup.
/// </summary>
public class BackupService
{
    private readonly NxClient _client;
    private readonly ILogger _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public BackupService(NxClient client, ILogger logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>
    /// Faz o backup da configuração completa de cada device, com concorrência limitada,
    /// e escreve o manifesto em &lt;rootDirectory&gt;/manifest.json.
    /// </summary>
    public async Task<List<BackupResult>> BackupAsync(
        List<JsonElement> devices, string serverName, string rootDirectory,
        int concurrency = 4, CancellationToken ct = default)
    {
        Directory.CreateDirectory(rootDirectory);

        var results = new ConcurrentBag<BackupResult>();
        var sem = new SemaphoreSlim(concurrency);
        int total = devices.Count, processed = 0;

        _logger.LogInfo($"Backup de {total} camera(s) em {serverName}...");

        var tasks = devices.Select(async d =>
        {
            await sem.WaitAsync(ct);
            try { results.Add(await BackupDeviceAsync(d, serverName, rootDirectory, ct)); }
            finally
            {
                sem.Release();
                Interlocked.Increment(ref processed);
                if (processed % 10 == 0 || processed == total) _logger.LogInfo($"{processed}/{total}.");
            }
        });
        await Task.WhenAll(tasks);

        var ordered = results.OrderBy(r => r.DeviceName).ToList();
        WriteManifest(ordered, serverName, rootDirectory);
        return ordered;
    }

    private async Task<BackupResult> BackupDeviceAsync(JsonElement d, string serverName, string rootDirectory, CancellationToken ct)
    {
        var id = NxClient.GetStringProperty(d, "id");
        var name = NxClient.GetStringProperty(d, "name");

        try
        {
            ct.ThrowIfCancellationRequested();
            var config = await _client.GetDeviceConfigAsync(id);

            var idClean = id.Trim('{', '}').Trim();
            var fileName = $"{Sanitize(name)}__{idClean[..Math.Min(8, idClean.Length)]}.json";
            var serverDir = Path.Combine(rootDirectory, Sanitize(serverName));
            Directory.CreateDirectory(serverDir);
            var filePath = Path.Combine(serverDir, fileName);

            File.WriteAllText(filePath, JsonSerializer.Serialize(config, JsonOptions));

            var size = new FileInfo(filePath).Length;
            _logger.LogSucesso($"Backup salvo: {Path.GetRelativePath(rootDirectory, filePath)}");
            return new BackupResult
            {
                DeviceId = id,
                DeviceName = name,
                ServerName = serverName,
                FilePath = filePath,
                Status = "success",
                SizeBytes = size
            };
        }
        catch (Exception ex)
        {
            _logger.LogErro($"{name}: falha no backup - {ex.Message}");
            return new BackupResult
            {
                DeviceId = id,
                DeviceName = name,
                ServerName = serverName,
                Status = "failed",
                ErrorMessage = ex.Message
            };
        }
    }

    private static void WriteManifest(List<BackupResult> results, string serverName, string rootDirectory)
    {
        var cameras = results.Select(r => new
        {
            deviceId = r.DeviceId,
            deviceName = r.DeviceName,
            file = string.IsNullOrEmpty(r.FilePath) ? string.Empty : Path.GetRelativePath(rootDirectory, r.FilePath),
            sizeBytes = r.SizeBytes,
            status = r.Status,
            error = r.ErrorMessage
        }).ToList();

        var manifest = new
        {
            generatedAtUtc = DateTime.UtcNow.ToString("o"),
            server = serverName,
            cameraCount = results.Count,
            cameras
        };

        File.WriteAllText(
            Path.Combine(rootDirectory, "manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOptions));
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "device" : cleaned;
    }
}
