using System.Collections.Concurrent;
using System.Text.Json;
using NxMassConfigurator.Models;

namespace NxMassConfigurator.Services;

public class DeviceProcessor
{
    private readonly NxConfigUpdater _u; private readonly ILogger _l;
    private readonly int _c, _r; private readonly CancellationToken _ct;

    public DeviceProcessor(NxConfigUpdater u, ILogger l, int c = 6, int r = 3, CancellationToken ct = default)
    { _u = u; _l = l; _c = c; _r = r; _ct = ct; }

    public async Task<List<OperationResult>> ProcessAllAsync(
        List<JsonElement> devs, string srv,
        double? fps, string? qual,
        bool unl, bool lok, bool ea, bool da, bool es, bool ds,
        string? codec, string? res, string? rtp, bool moff, bool mon,
        bool dry)
    {
        var results = new ConcurrentBag<OperationResult>();
        var sem = new SemaphoreSlim(_c); int tot = devs.Count, prc = 0;

        var desc = new List<string>();
        if (fps.HasValue) desc.Add($"FPS→{fps}"); if (!string.IsNullOrWhiteSpace(qual)) desc.Add($"Q→{qual}");
        if (unl) desc.Add("Stream"); if (lok) desc.Add("Stream+"); if (ea) desc.Add("A+"); if (da) desc.Add("A-");
        if (es) desc.Add("Sec+"); if (ds) desc.Add("Sec-"); if (!string.IsNullOrWhiteSpace(codec)) desc.Add(codec);
        if (!string.IsNullOrWhiteSpace(res)) desc.Add(res); if (!string.IsNullOrWhiteSpace(rtp)) desc.Add($"RTP:{rtp}");
        if (moff) desc.Add("Mot-"); if (mon) desc.Add("Mot+");

        _l.LogInfo(dry ? $"DRY-RUN {tot} cams ({string.Join(", ", desc)})..." : $"Processando {tot} cams ({string.Join(", ", desc)})...");

        var tasks = devs.Select(async d =>
        {
            await sem.WaitAsync(_ct);
            try { results.Add(await Rx(d, srv, fps, qual, unl, lok, ea, da, es, ds, codec, res, rtp, moff, mon, dry)); }
            finally { sem.Release(); Interlocked.Increment(ref prc); if (prc % 10 == 0 || prc == tot) _l.LogInfo($"{prc}/{tot}."); }
        });
        await Task.WhenAll(tasks);
        return results.OrderBy(r => r.DeviceName).ToList();
    }

    private async Task<OperationResult> Rx(JsonElement d, string srv, double? fps, string? qual,
        bool unl, bool lok, bool ea, bool da, bool es, bool ds,
        string? codec, string? res, string? rtp, bool moff, bool mon, bool dry)
    {
        var id = NxClient.GetStringProperty(d, "id"); var nm = NxClient.GetStringProperty(d, "name");
        for (int a = 0; a <= _r; a++)
        {
            try
            {
                if (_ct.IsCancellationRequested) return F(id, nm, "Cancelado.");
                var r = await _u.UpdateDeviceAsync(d, srv, fps, qual, unl, lok, ea, da, es, ds, codec, res, rtp, moff, mon, dry);
                if (r.IsSuccess || r.IsDryRun || r.Status == "skipped") return r;
                if (a < _r && T(r.ErrorMessage)) { var w = W(a); _l.LogWarn($"{nm}: T{a + 1}/{_r}. {w.TotalSeconds:F1}s..."); await Task.Delay(w, _ct); continue; }
                return r;
            }
            catch (Exception ex) { if (a >= _r) return F(id, nm, $"Falha: {ex.Message}"); await Task.Delay(W(a), _ct); }
        }
        return F(id, nm, "Max retries.");
    }
    static TimeSpan W(int a) => TimeSpan.FromSeconds(Math.Pow(2, a + 1) + Random.Shared.NextDouble());
    static OperationResult F(string i, string n, string m) => new() { DeviceId = i, DeviceName = n, Status = "failed", ErrorMessage = m };
    static bool T(string? m) => m?.Contains("429") == true || m?.Contains("Timeout") == true || m?.Contains("503") == true;
}
