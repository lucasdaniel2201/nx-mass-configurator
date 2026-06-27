using System.Text.Json;
using System.Text.Json.Nodes;

namespace NxMassConfigurator.Services;

/// <summary>
/// Responsável por localizar e modificar FPS e Recording Schedule no JSON do device.
/// Navegação genérica (JsonNode/JsonElement) — resiliente a variações de schema NX 5.x / 6.x.
///
/// NX 6.x: FPS fica em "schedule.tasks[].fps" (objeto schedule → array tasks)
/// NX 5.x: pode estar em "scheduleTasks[].fps" (array direto no raiz)
/// </summary>
public class ScheduleBuilder
{
    private readonly ILogger _logger;

    public ScheduleBuilder(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Localiza o campo de FPS no JSON do device e retorna o valor atual.
    /// Tenta root-level e também dentro de schedule.tasks[0].fps (NX 6.x).
    /// </summary>
    public (double? fps, string fieldName) ExtractFps(JsonElement config)
    {
        // Root-level candidates
        string[] rootCandidates = ["fps", "maxFps", "recordingFps", "defaultFps"];

        foreach (var field in rootCandidates)
        {
            var val = NxClient.GetDoubleProperty(config, field);
            if (val.HasValue)
                return (val.Value, field);
        }

        // NX 6.x: try schedule.tasks[0].fps
        if (config.TryGetProperty("schedule", out var scheduleEl) &&
            scheduleEl.ValueKind == JsonValueKind.Object &&
            scheduleEl.TryGetProperty("tasks", out var tasksEl) &&
            tasksEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var task in tasksEl.EnumerateArray())
            {
                var fpsVal = NxClient.GetDoubleProperty(task, "fps");
                if (fpsVal.HasValue)
                    return (fpsVal.Value, "schedule");  // return marker that FPS is inside schedule
            }
        }

        return (null, string.Empty);
    }

    /// <summary>
    /// Localiza e modifica o FPS no JSON do device (mutação in-place via JsonNode).
    /// Se fieldName estiver vazio OU for "schedule", apenas atualiza internamente no schedule.
    /// </summary>
    public double? SetFps(JsonObject root, string fieldName, double newFps)
    {
        // Se o FPS está dentro do schedule (NX 6.x), não tenta criar campo root.
        if (fieldName == "schedule")
            return null;

        // Se fieldName é vazio, não faz nada no root.
        if (string.IsNullOrEmpty(fieldName))
            return null;

        if (root.TryGetPropertyValue(fieldName, out var node) && node is JsonValue jv)
        {
            double? old = null;
            if (jv.TryGetValue(out double d))
                old = d;

            root[fieldName] = JsonValue.Create(newFps);
            return old;
        }

        root[fieldName] = JsonValue.Create(newFps);
        return null;
    }

    /// <summary>
    /// Localiza o recording schedule no JSON e atualiza o FPS em cada entrada.
    /// NX 6.x: root.schedule.tasks[]  (objeto → array)
    /// NX 5.x: root.scheduleTasks[]   (array direto)
    /// </summary>
    public double? UpdateScheduleFps(JsonObject root, double newFps)
    {
        double? oldScheduleFps = null;

        // ── Pattern 1: NX 6.x — schedule é objeto com tasks array ──
        if (root.TryGetPropertyValue("schedule", out var scheduleNode) &&
            scheduleNode is JsonObject scheduleObj)
        {
            if (scheduleObj.TryGetPropertyValue("tasks", out var tasksNode) &&
                tasksNode is JsonArray tasksArray)
            {
                foreach (var item in tasksArray)
                {
                    if (item is JsonObject task)
                        UpdateFpsInScheduleTask(task, newFps, ref oldScheduleFps);
                }
            }
        }

        // ── Pattern 2: NX 5.x — array direto no raiz ──
        string[] flatArrayCandidates = ["scheduleTasks", "recordingSchedule", "tasks"];

        foreach (var candidate in flatArrayCandidates)
        {
            if (root.TryGetPropertyValue(candidate, out var node) && node is JsonArray array)
            {
                foreach (var item in array)
                {
                    if (item is JsonObject task)
                        UpdateFpsInScheduleTask(task, newFps, ref oldScheduleFps);
                }
            }
        }

        return oldScheduleFps;
    }

    /// <summary>
    /// Atualiza o FPS em uma entrada do schedule (task individual).
    /// Também garante que campos obrigatórios do NX 6.x existam (ex: recordingType).
    /// </summary>
    private static void UpdateFpsInScheduleTask(JsonObject task, double newFps, ref double? oldFps)
    {
        if (task.TryGetPropertyValue("fps", out var fpsNode) && fpsNode is JsonValue fpsVal)
        {
            if (fpsVal.TryGetValue(out double old) && !oldFps.HasValue)
                oldFps = old;

            task["fps"] = JsonValue.Create(newFps);
        }
        else
        {
            // Se não tem campo fps, cria
            task["fps"] = JsonValue.Create(newFps);
        }

        // ── Campos obrigatórios NX 6.x ──
        // GET pode não retornar recordingType, mas PUT exige.
        EnsureStringProperty(task, "recordingType", "always");

        // recordAfter.fps
        if (task.TryGetPropertyValue("recordAfter", out var raNode) && raNode is JsonObject raObj)
        {
            if (raObj.TryGetPropertyValue("fps", out var raFpsNode) && raFpsNode is JsonValue raFpsVal)
            {
                if (raFpsVal.TryGetValue(out double old) && !oldFps.HasValue)
                    oldFps = old;
                raObj["fps"] = JsonValue.Create(newFps);
            }
        }

        // recordBefore.fps
        if (task.TryGetPropertyValue("recordBefore", out var rbNode) && rbNode is JsonObject rbObj)
        {
            if (rbObj.TryGetPropertyValue("fps", out var rbFpsNode) && rbFpsNode is JsonValue rbFpsVal)
            {
                if (rbFpsVal.TryGetValue(out double old) && !oldFps.HasValue)
                    oldFps = old;
                rbObj["fps"] = JsonValue.Create(newFps);
            }
        }
    }

    /// <summary>
    /// Garante que uma propriedade string exista no JsonObject. Se não existir, cria com defaultValue.
    /// </summary>
    private static void EnsureStringProperty(JsonObject obj, string propertyName, string defaultValue)
    {
        if (!obj.ContainsKey(propertyName))
        {
            obj[propertyName] = JsonValue.Create(defaultValue);
        }
    }

    /// <summary>
    /// Converte um JsonElement em JsonNode mutável.
    /// </summary>
    public static JsonObject ToMutable(JsonElement element)
    {
        var node = JsonNode.Parse(element.GetRawText());
        return (node as JsonObject) ?? new JsonObject();
    }

    /// <summary>
    /// Serializa para JSON string.
    /// </summary>
    public static string ToJson(JsonObject node)
    {
        return node.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }
}
