using System.Collections.Concurrent;
using System.Text;
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
    private readonly ScheduleBuilder _scheduleBuilder;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public BackupService(NxClient client, ILogger logger)
    {
        _client = client;
        _logger = logger;
        _scheduleBuilder = new ScheduleBuilder(logger);
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
        WriteCsv(ordered, rootDirectory);
        _logger.LogSucesso($"Planilha do backup: {Path.Combine(rootDirectory, "cameras.csv")}");
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
                SizeBytes = size,
                Resolution = ExtractResolution(config),
                Fps = ExtractFps(config),
                Codec = ExtractCodec(config),
                BitrateKbps = ExtractBitrateKbps(config)
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

    // ─── Extração de dados de vídeo da configuração ───────────────────

    /// <summary>
    /// Resolução: parameters.primaryStreamConfiguration.resolution → parameters.resolution → vazio.
    /// Nunca lança exceção por campo ausente.
    /// </summary>
    private static string ExtractResolution(JsonElement config)
    {
        if (config.TryGetProperty("parameters", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            if (p.TryGetProperty("primaryStreamConfiguration", out var primary) && primary.ValueKind == JsonValueKind.Object)
            {
                var resolution = TryGetResolution(primary);
                if (resolution.Length > 0) return resolution;
            }

            var fallback = TryGetResolution(p);
            if (fallback.Length > 0) return fallback;
        }

        return string.Empty;
    }

    private static string TryGetResolution(JsonElement container)
    {
        if (container.TryGetProperty("resolution", out var r) &&
            r.ValueKind == JsonValueKind.Object &&
            r.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number &&
            r.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number)
        {
            return $"{w.GetInt32()}x{h.GetInt32()}";
        }

        return string.Empty;
    }

    /// <summary>
    /// Codec: parameters.primaryStreamConfiguration.codec → parameters.codec → vazio.
    /// </summary>
    private static string ExtractCodec(JsonElement config)
    {
        if (config.TryGetProperty("parameters", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            if (p.TryGetProperty("primaryStreamConfiguration", out var primary) && primary.ValueKind == JsonValueKind.Object)
            {
                var codec = NxClient.GetStringProperty(primary, "codec");
                if (codec.Length > 0) return codec;
            }

            var fallback = NxClient.GetStringProperty(p, "codec");
            if (fallback.Length > 0) return fallback;
        }

        return string.Empty;
    }

    /// <summary>
    /// Bitrate em kbps: parameters.bitrateKbps → parameters.primaryStreamConfiguration.bitrateKbps
    /// → parameters.bitrate → null. Aceita apenas número &gt; 0.
    /// </summary>
    private static long? ExtractBitrateKbps(JsonElement config)
    {
        if (!config.TryGetProperty("parameters", out var p) || p.ValueKind != JsonValueKind.Object)
            return null;

        var candidates = new List<long> { NxClient.GetLongProperty(p, "bitrateKbps") };

        if (p.TryGetProperty("primaryStreamConfiguration", out var primary) && primary.ValueKind == JsonValueKind.Object)
            candidates.Add(NxClient.GetLongProperty(primary, "bitrateKbps"));

        candidates.Add(NxClient.GetLongProperty(p, "bitrate"));

        foreach (var candidate in candidates)
        {
            if (candidate > 0) return candidate;
        }

        return null;
    }

    /// <summary>
    /// FPS: ScheduleBuilder.ExtractFps (schedule.tasks[] e campos raiz) →
    /// parameters.primaryStreamConfiguration.fps → parameters.fps → null.
    /// </summary>
    private double? ExtractFps(JsonElement config)
    {
        var (fps, _) = _scheduleBuilder.ExtractFps(config);
        if (fps.HasValue) return fps;

        if (config.TryGetProperty("parameters", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            if (p.TryGetProperty("primaryStreamConfiguration", out var primary) && primary.ValueKind == JsonValueKind.Object)
            {
                var primaryFps = NxClient.GetDoubleProperty(primary, "fps");
                if (primaryFps.HasValue) return primaryFps;
            }

            var fallback = NxClient.GetDoubleProperty(p, "fps");
            if (fallback.HasValue) return fallback;
        }

        return null;
    }

    // ─── Planilha cameras.csv ────────────────────────────────────────

    /// <summary>
    /// Gera cameras.csv na raiz do backup: uma linha por câmera,
    /// separador ";" (Excel pt-BR), BOM UTF-8 e quebras CRLF.
    /// </summary>
    private static void WriteCsv(List<BackupResult> results, string rootDirectory)
    {
        var sb = new StringBuilder();
        sb.Append("Camera;Servidor;Resolucao;FPS;Codec;Bitrate (kbps);Arquivo;Erro\r\n");

        foreach (var r in results)
        {
            var failed = r.IsFailed;
            var file = string.IsNullOrEmpty(r.FilePath)
                ? string.Empty
                : Path.GetRelativePath(rootDirectory, r.FilePath);

            var cells = new[]
            {
                r.DeviceName,
                r.ServerName,
                failed ? string.Empty : r.Resolution,
                failed ? string.Empty : r.Fps?.ToString("0.##") ?? string.Empty,
                failed ? string.Empty : r.Codec,
                failed ? string.Empty : r.BitrateKbps?.ToString() ?? string.Empty,
                failed ? string.Empty : file,
                r.ErrorMessage ?? string.Empty
            };

            sb.Append(string.Join(';', cells.Select(EscapeCsv)));
            sb.Append("\r\n");
        }

        File.WriteAllText(
            Path.Combine(rootDirectory, "cameras.csv"),
            sb.ToString(),
            new UTF8Encoding(true));
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains(';') || value.Contains('"') || value.Contains('\r') || value.Contains('\n'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";

        return value;
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "device" : cleaned;
    }
}
