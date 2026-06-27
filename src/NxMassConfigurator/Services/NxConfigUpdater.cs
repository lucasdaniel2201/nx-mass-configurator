using System.Text.Json;
using System.Text.Json.Nodes;
using NxMassConfigurator.Exceptions;
using NxMassConfigurator.Models;

namespace NxMassConfigurator.Services;

/// <summary>
/// Orquestra a leitura e atualização da configuração de um device no NX Witness.
/// Implementa o padrão read-modify-write com JsonNode mutável.
/// Suporta alteração de FPS, qualidade de gravação, ou ambos.
/// </summary>
public class NxConfigUpdater
{
    private readonly NxClient _client;
    private readonly ScheduleBuilder _scheduleBuilder;
    private readonly QualityUpdater _qualityUpdater;
    private readonly ILogger _logger;

    public NxConfigUpdater(NxClient client, ILogger logger)
    {
        _client = client;
        _scheduleBuilder = new ScheduleBuilder(logger);
        _qualityUpdater = new QualityUpdater(logger);
        _logger = logger;
    }

    /// <summary>
    /// Executa o ciclo read-modify-write (ou dry-run) para um device.
    /// Altera FPS e/ou qualidade de gravação, conforme especificado.
    /// Passe null para o parâmetro que não deseja alterar.
    /// </summary>
    public async Task<OperationResult> UpdateDeviceAsync(
        JsonElement deviceSummary, string serverName,
        double? targetFps, string? targetQuality,
        bool unlockStream, bool lockStream,
        bool enableAudio, bool disableAudio,
        bool enableSecondary, bool disableSecondary,
        string? codec, string? resolution,
        bool dryRun)
    {
        var deviceId = NxClient.GetStringProperty(deviceSummary, "id");
        var deviceName = NxClient.GetStringProperty(deviceSummary, "name");

        var result = new OperationResult
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            NewFps = targetFps,
            Status = "dry-run"
        };

        try
        {
            // Step 1: GET full device config
            JsonElement fullConfig;
            try
            {
                fullConfig = await _client.GetDeviceConfigAsync(deviceId);
            }
            catch (NxApiException ex) when (ex.HttpStatusCode == 404)
            {
                result.Status = "failed";
                result.ErrorMessage = $"Device {deviceId} não encontrado (404).";
                _logger.LogErro($"{deviceName}: {result.ErrorMessage}");
                return result;
            }

            // Step 2: Parse into mutable JsonObject
            var configRoot = ScheduleBuilder.ToMutable(fullConfig);
            bool anyChange = false;

            // ── FPS ──
            if (targetFps.HasValue)
            {
                var (oldFps, fpsField) = _scheduleBuilder.ExtractFps(fullConfig);
                result.OldFps = oldFps;

                if (oldFps.HasValue)
                    _logger.LogInfo($"  {deviceName}: FPS atual = {oldFps.Value} (fonte: {(fpsField == "schedule" ? "schedule.tasks" : $"root.{fpsField}")})");
                else
                    _logger.LogInfo($"  {deviceName}: FPS não encontrado no root — será atualizado no schedule.");

                if (oldFps.HasValue && Math.Abs(oldFps.Value - targetFps.Value) < 0.01)
                {
                    _logger.LogInfo($"  {deviceName}: FPS já é {targetFps} — sem alteração.");
                }
                else
                {
                    _scheduleBuilder.SetFps(configRoot, fpsField, targetFps.Value);
                    var schedFpsBefore = _scheduleBuilder.UpdateScheduleFps(configRoot, targetFps.Value);
                    if (!result.OldFps.HasValue && schedFpsBefore.HasValue)
                        result.OldFps = schedFpsBefore;
                    anyChange = true;
                }
            }

            // ── Quality ──
            if (!string.IsNullOrWhiteSpace(targetQuality))
            {
                var targetValue = QualityUpdater.MapStreamQuality(targetQuality);
                var oldQuality = _qualityUpdater.ExtractQuality(configRoot);

                if (oldQuality != null)
                    _logger.LogInfo($"  {deviceName}: Qualidade atual = {oldQuality}");
                else
                    _logger.LogInfo($"  {deviceName}: Qualidade não definida — será {targetValue}.");

                if (oldQuality != null && string.Equals(oldQuality, targetValue, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInfo($"  {deviceName}: Qualidade já é {targetValue} — sem alteração.");
                }
                else
                {
                    _qualityUpdater.UpdateQuality(configRoot, targetQuality);
                    anyChange = true;
                }
            }

            // ── Stream lock/unlock ──
            if (unlockStream || lockStream)
            {
                if (configRoot.TryGetPropertyValue("options", out var optNode) && optNode is JsonObject optObj)
                {
                    if (unlockStream)
                    {
                        // Desmarcar checkbox: remover isControlEnabled (NX gerencia)
                        if (optObj.ContainsKey("isControlEnabled"))
                        {
                            optObj.Remove("isControlEnabled");
                            _logger.LogInfo($"  {deviceName}: Stream → DESMARCADO (isControlEnabled removido).");
                        }
                        else
                        {
                            _logger.LogInfo($"  {deviceName}: Stream já está desmarcado (isControlEnabled ausente).");
                        }
                    }
                    else // lockStream
                    {
                        // Marcar checkbox: isControlEnabled = false (câmera gerencia)
                        var hadIt = optObj.ContainsKey("isControlEnabled");
                        optObj["isControlEnabled"] = JsonValue.Create(false);
                        _logger.LogInfo($"  {deviceName}: Stream → MARCADO (isControlEnabled = false){(hadIt ? " (já estava)" : "")}.");
                    }
                    anyChange = true;
                }
                else
                {
                    _logger.LogWarn($"  {deviceName}: Campo 'options' não encontrado no JSON.");
                }
            }

            // ── Audio enable/disable ──
            if (enableAudio || disableAudio)
            {
                HandleBoolParam(configRoot, "parameters", "dontRecordAudio",
                    enableAudio ? null : 1,  // enable → remove; disable → 1
                    ref anyChange);
                if (enableAudio)
                    _logger.LogInfo($"  {deviceName}: Áudio → HABILITADO (dontRecordAudio removido).");
                else
                    _logger.LogInfo($"  {deviceName}: Áudio → DESABILITADO (dontRecordAudio = 1).");
            }

            // ── Secondary stream enable/disable ──
            if (enableSecondary || disableSecondary)
            {
                HandleBoolParam(configRoot, "parameters", "dontRecordSecondaryStream",
                    enableSecondary ? null : 1,  // enable → remove; disable → 1
                    ref anyChange);
                if (enableSecondary)
                    _logger.LogInfo($"  {deviceName}: Stream secundário → HABILITADO (dontRecordSecondaryStream removido).");
                else
                    _logger.LogInfo($"  {deviceName}: Stream secundário → DESABILITADO (dontRecordSecondaryStream = 1).");
            }

            // ── Codec ──
            if (!string.IsNullOrWhiteSpace(codec))
            {
                var targetCodec = codec.ToUpperInvariant();
                if (configRoot.TryGetPropertyValue("parameters", out var pNode3) && pNode3 is JsonObject pObj3 &&
                    pObj3.TryGetPropertyValue("primaryStreamConfiguration", out var pscNode3) && pscNode3 is JsonObject pscObj3)
                {
                    var oldCodec = "?";
                    if (pscObj3.TryGetPropertyValue("codec", out var cNode) && cNode is JsonValue cVal)
                        oldCodec = cVal.GetValue<string>() ?? "?";

                    if (string.Equals(oldCodec, targetCodec, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInfo($"  {deviceName}: Codec já é {targetCodec}.");
                    }
                    else
                    {
                        pscObj3["codec"] = JsonValue.Create(targetCodec);
                        _logger.LogInfo($"  {deviceName}: Codec: {oldCodec} → {targetCodec}.");
                        anyChange = true;
                    }
                }
            }

            // ── Resolution ──
            if (!string.IsNullOrWhiteSpace(resolution))
            {
                var parts = resolution.Split('x', 'X', '*');
                if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
                {
                    if (configRoot.TryGetPropertyValue("parameters", out var pNode4) && pNode4 is JsonObject pObj4 &&
                        pObj4.TryGetPropertyValue("primaryStreamConfiguration", out var pscNode4) && pscNode4 is JsonObject pscObj4 &&
                        pscObj4.TryGetPropertyValue("resolution", out var resNode) && resNode is JsonObject resObj)
                    {
                        var oldW = "?"; var oldH = "?";
                        if (resObj.TryGetPropertyValue("width", out var wNode) && wNode is JsonValue wVal)
                            oldW = wVal.GetValue<int>().ToString();
                        if (resObj.TryGetPropertyValue("height", out var hNode) && hNode is JsonValue hVal)
                            oldH = hVal.GetValue<int>().ToString();

                        if (oldW == w.ToString() && oldH == h.ToString())
                        {
                            _logger.LogInfo($"  {deviceName}: Resolução já é {w}x{h}.");
                        }
                        else
                        {
                            resObj["width"] = JsonValue.Create(w);
                            resObj["height"] = JsonValue.Create(h);
                            _logger.LogInfo($"  {deviceName}: Resolução: {oldW}x{oldH} → {w}x{h}.");
                            anyChange = true;
                        }
                    }
                }
                else
                {
                    _logger.LogWarn($"  {deviceName}: Formato inválido: '{resolution}'. Use 1920x1080.");
                }
            }

            // ── Ensure mandatory fields in schedule tasks (recordingType required for PUT) ──
            EnsureRecordingTypeInSchedule(configRoot);

            // ── No changes? ──
            if (!anyChange)
            {
                result.Status = "skipped";
                _logger.LogInfo($"{deviceName}: Nenhuma alteração necessária.");
                return result;
            }

            // ── Dry-run ──
            if (dryRun)
            {
                result.Status = "dry-run";
                var parts = new List<string>();
                if (targetFps.HasValue) parts.Add($"FPS: {result.OldFps?.ToString() ?? "N/A"} → {targetFps}");
                if (!string.IsNullOrWhiteSpace(targetQuality)) parts.Add($"Qualidade → {targetQuality}");
                _logger.LogInfo($"[DRY-RUN] {deviceName}: {string.Join(" | ", parts)}");
                return result;
            }

            // ── PUT ──
            var jsonBody = ScheduleBuilder.ToJson(configRoot);
            try
            {
                await _client.UpdateDeviceAsync(deviceId, jsonBody);
                result.Status = "success";
                var parts = new List<string>();
                if (targetFps.HasValue) parts.Add($"FPS {result.OldFps?.ToString() ?? "N/A"} → {targetFps}");
                if (!string.IsNullOrWhiteSpace(targetQuality)) parts.Add($"Qualidade → {targetQuality}");
                _logger.LogSucesso($"{deviceName}: {string.Join(" | ", parts)}");
            }
            catch (NxApiException ex)
            {
                result.Status = "failed";
                result.ErrorMessage = ex.Message;
                result.HttpStatus = ex.HttpStatusCode;
                _logger.LogErro($"{deviceName}: Falha ao aplicar — {ex.Message}");

                var scheduleIdx = jsonBody.IndexOf("\"schedule\"", StringComparison.OrdinalIgnoreCase);
                if (scheduleIdx >= 0)
                {
                    var start = Math.Max(0, scheduleIdx - 20);
                    var snippet = jsonBody.Substring(start, Math.Min(600, jsonBody.Length - start));
                    _logger.LogInfo($"  Trecho do schedule: ...{snippet}...");
                }
            }
        }
        catch (NxTimeoutException ex)
        {
            result.Status = "failed";
            result.ErrorMessage = ex.Message;
            _logger.LogErro($"{deviceName}: Timeout — {ex.Message}");
        }
        catch (NxConnectionException ex)
        {
            result.Status = "failed";
            result.ErrorMessage = ex.Message;
            _logger.LogErro($"{deviceName}: Conexão — {ex.Message}");
        }
        catch (Exception ex)
        {
            result.Status = "failed";
            result.ErrorMessage = $"Erro inesperado: {ex.Message}";
            _logger.LogErro($"{deviceName}: {result.ErrorMessage}");
        }

        return result;
    }

    /// <summary>
    /// Garante que recordingType exista em todos os schedule tasks (obrigatório para PUT).
    /// </summary>
    private static void EnsureRecordingTypeInSchedule(JsonObject root)
    {
        if (root.TryGetPropertyValue("schedule", out var schedNode) &&
            schedNode is JsonObject schedObj &&
            schedObj.TryGetPropertyValue("tasks", out var tasksNode) &&
            tasksNode is JsonArray tasksArray)
        {
            foreach (var item in tasksArray)
            {
                if (item is JsonObject task && !task.ContainsKey("recordingType"))
                    task["recordingType"] = JsonValue.Create("always");
            }
        }
    }

    /// <summary>
    /// Helper genérico: remove ou seta um campo booleano (0/1 style) dentro de um objeto pai no JSON.
    /// parentName = "parameters", fieldName = "dontRecordAudio", value = null (remove) or int (set).
    /// </summary>
    private static void HandleBoolParam(JsonObject root, string parentName, string fieldName, int? value, ref bool changed)
    {
        if (root.TryGetPropertyValue(parentName, out var parentNode) && parentNode is JsonObject parentObj)
        {
            if (value.HasValue)
            {
                // Seta o campo para o valor (1 ou 0)
                var hadIt = parentObj.ContainsKey(fieldName);
                parentObj[fieldName] = JsonValue.Create(value.Value);
                if (!hadIt) changed = true;
            }
            else
            {
                // Remove o campo (desmarcar checkbox)
                if (parentObj.ContainsKey(fieldName))
                {
                    parentObj.Remove(fieldName);
                    changed = true;
                }
            }
        }
    }
}
