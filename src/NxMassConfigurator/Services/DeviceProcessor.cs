using System.Collections.Concurrent;
using System.Text.Json;
using NxMassConfigurator.Models;

namespace NxMassConfigurator.Services;

public class DeviceProcessor
{
    private readonly NxConfigUpdater _updater;
    private readonly ILogger _logger;
    private readonly int _maxConcurrency;
    private readonly int _maxRetries;
    private readonly CancellationToken _cancellationToken;

    public DeviceProcessor(NxConfigUpdater updater, ILogger logger,
        int maxConcurrency = 6, int maxRetries = 3, CancellationToken ct = default)
    {
        _updater = updater; _logger = logger;
        _maxConcurrency = maxConcurrency; _maxRetries = maxRetries;
        _cancellationToken = ct;
    }

    public async Task<List<OperationResult>> ProcessAllAsync(
        List<JsonElement> devices, string serverName,
        double? targetFps, string? targetQuality,
        bool unlockStream, bool lockStream,
        bool enableAudio, bool disableAudio,
        bool enableSecondary, bool disableSecondary,
        string? codec, string? resolution,
        bool dryRun)
    {
        var results = new ConcurrentBag<OperationResult>();
        var semaphore = new SemaphoreSlim(_maxConcurrency);
        int total = devices.Count, processed = 0;

        var desc = new List<string>();
        if (targetFps.HasValue) desc.Add($"FPS→{targetFps}");
        if (!string.IsNullOrWhiteSpace(targetQuality)) desc.Add($"Q→{targetQuality}");
        if (unlockStream) desc.Add("Stream→desmarcar");
        if (lockStream) desc.Add("Stream→marcar");
        if (enableAudio) desc.Add("Áudio→hab");
        if (disableAudio) desc.Add("Áudio→desab");
        if (enableSecondary) desc.Add("Sec→hab");
        if (disableSecondary) desc.Add("Sec→desab");
        if (!string.IsNullOrWhiteSpace(codec)) desc.Add($"Codec→{codec}");
        if (!string.IsNullOrWhiteSpace(resolution)) desc.Add($"Res→{resolution}");

        _logger.LogInfo(dryRun
            ? $"Processando {total} câmeras em DRY-RUN ({string.Join(", ", desc)})..."
            : $"Processando {total} câmeras ({string.Join(", ", desc)})...");

        var tasks = devices.Select(async device =>
        {
            await semaphore.WaitAsync(_cancellationToken);
            try
            {
                var r = await ProcessWithRetryAsync(device, serverName, targetFps, targetQuality,
                    unlockStream, lockStream, enableAudio, disableAudio, enableSecondary, disableSecondary,
                    codec, resolution, dryRun);
                results.Add(r);
            }
            finally
            {
                semaphore.Release();
                Interlocked.Increment(ref processed);
                if (processed % 10 == 0 || processed == total)
                    _logger.LogInfo($"Progresso: {processed}/{total}.");
            }
        });
        await Task.WhenAll(tasks);
        return results.OrderBy(r => r.DeviceName).ToList();
    }

    private async Task<OperationResult> ProcessWithRetryAsync(
        JsonElement device, string serverName,
        double? targetFps, string? targetQuality,
        bool unlockStream, bool lockStream,
        bool enableAudio, bool disableAudio,
        bool enableSecondary, bool disableSecondary,
        string? codec, string? resolution,
        bool dryRun)
    {
        var deviceId = NxClient.GetStringProperty(device, "id");
        var deviceName = NxClient.GetStringProperty(device, "name");

        for (int attempt = 0; attempt <= _maxRetries; attempt++)
        {
            try
            {
                if (_cancellationToken.IsCancellationRequested)
                    return Fail(deviceId, deviceName, "Cancelado.");

                var result = await _updater.UpdateDeviceAsync(device, serverName, targetFps, targetQuality,
                    unlockStream, lockStream, enableAudio, disableAudio, enableSecondary, disableSecondary,
                    codec, resolution, dryRun);

                if (result.IsSuccess || result.IsDryRun || result.Status == "skipped") return result;

                if (attempt < _maxRetries && Transient(result.ErrorMessage))
                {
                    var d = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1) + Random.Shared.NextDouble());
                    _logger.LogWarn($"{deviceName}: Tentativa {attempt + 1}/{_maxRetries}. Retry {d.TotalSeconds:F1}s...");
                    await Task.Delay(d, _cancellationToken); continue;
                }
                return result;
            }
            catch (Exception ex)
            {
                if (attempt >= _maxRetries) return Fail(deviceId, deviceName, $"Falha ({_maxRetries} tentativas): {ex.Message}");
                var d = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1) + Random.Shared.NextDouble());
                _logger.LogWarn($"{deviceName}: Erro (tentativa {attempt + 1}/{_maxRetries}). Retry {d.TotalSeconds:F1}s...");
                await Task.Delay(d, _cancellationToken);
            }
        }
        return Fail(deviceId, deviceName, "Máximo de retries.");
    }

    private static OperationResult Fail(string id, string name, string msg) =>
        new() { DeviceId = id, DeviceName = name, Status = "failed", ErrorMessage = msg };

    private static bool Transient(string? msg) =>
        msg?.Contains("429") == true || msg?.Contains("Timeout") == true || msg?.Contains("503") == true;
}
