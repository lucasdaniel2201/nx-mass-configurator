using System.Text.Json.Nodes;

namespace NxMassConfigurator.Services;

/// <summary>
/// Responsável por localizar e atualizar a qualidade de gravação no JSON do device.
///
/// NX 6.x + Dahua: a qualidade do arquivo gravado é controlada EXCLUSIVAMENTE por
///   schedule.tasks[].streamQuality
///
/// Valores aceitos (confirmados via inspect):
///   "low"      → Baixa    (usa stream secundário)
///   "normal"   → Média    (NÃO "medium" — o servidor rejeita "medium")
///   "high"     → Alta     (stream primário, qualidade máxima)
///   "highest"  → Altíssima
///
/// NÃO mexe em: bitrateKbps, bitrateFactor, primaryStreamConfiguration.quality, task.quality.
/// </summary>
public class QualityUpdater
{
    private readonly ILogger _logger;

    public QualityUpdater(ILogger logger) => _logger = logger;

    /// <summary>
    /// Mapeia input do usuário para o valor aceito pelo NX/Dahua.
    /// </summary>
    public static string MapStreamQuality(string input)
    {
        return input?.ToLowerInvariant() switch
        {
            "low" or "baixa" or "l"                             => "low",
            "medium" or "med" or "média" or "media" or "m"      => "normal",
            "high" or "alta" or "h"                             => "high",
            "highest" or "altíssima" or "maxima" or "máxima"    => "highest",
            _ => "normal"
        };
    }

    /// <summary>
    /// Extrai o valor atual de streamQuality do schedule.
    /// </summary>
    public string? ExtractQuality(JsonObject root)
    {
        if (root.TryGetPropertyValue("schedule", out var schedNode) &&
            schedNode is JsonObject schedObj &&
            schedObj.TryGetPropertyValue("tasks", out var tasksNode) &&
            tasksNode is JsonArray tasksArray)
        {
            foreach (var item in tasksArray)
            {
                if (item is JsonObject task &&
                    task.TryGetPropertyValue("streamQuality", out var sqNode) &&
                    sqNode is JsonValue sqVal)
                {
                    return sqVal.GetValue<string>();
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Atualiza APENAS schedule.tasks[].streamQuality em todos os tasks.
    /// Também garante que recordingType exista (obrigatório para PUT).
    /// </summary>
    public string? UpdateQuality(JsonObject root, string qualityInput)
    {
        var targetValue = MapStreamQuality(qualityInput);
        string? oldValue = ExtractQuality(root);
        bool foundAny = false;

        if (root.TryGetPropertyValue("schedule", out var schedNode) &&
            schedNode is JsonObject schedObj &&
            schedObj.TryGetPropertyValue("tasks", out var tasksNode) &&
            tasksNode is JsonArray tasksArray)
        {
            foreach (var item in tasksArray)
            {
                if (item is JsonObject task)
                {
                    // recordingType obrigatório para PUT (GET pode não devolver)
                    if (!task.ContainsKey("recordingType"))
                        task["recordingType"] = JsonValue.Create("always");

                    task["streamQuality"] = JsonValue.Create(targetValue);
                    foundAny = true;
                }
            }
        }

        if (foundAny)
            _logger.LogInfo($"  streamQuality: {(oldValue ?? "?")} → {targetValue}");
        else
            _logger.LogWarn("  schedule.tasks não encontrado no JSON.");

        return foundAny ? oldValue : null;
    }
}
