using System.Text;
using NxMassConfigurator.Models;

namespace NxMassConfigurator.Services;

/// <summary>
/// Gera relatório CSV e resumo em console.
/// </summary>
public class ReportWriter
{
    private readonly ILogger _logger;

    public ReportWriter(ILogger logger) => _logger = logger;

    /// <summary>
    /// Escreve o relatório final: resumo no console + arquivo CSV (se path fornecido).
    /// </summary>
    public void Write(List<OperationResult> results, string? outputPath,
        string serverName, double? fps, string? quality, bool dryRun)
    {
        var total = results.Count;
        var success = results.Count(r => r.IsSuccess);
        var dryRuns = results.Count(r => r.IsDryRun);
        var failed = results.Count(r => r.IsFailed);
        var skipped = results.Count(r => r.Status == "skipped");

        Console.WriteLine();
        Console.WriteLine(new string('═', 60));
        Console.WriteLine($"  RESUMO — Servidor: {serverName}");
        Console.WriteLine(new string('═', 60));
        Console.WriteLine($"  Modo:         {(dryRun ? "DRY-RUN (simulação)" : "EXECUÇÃO REAL")}");
        if (fps.HasValue) Console.WriteLine($"  FPS alvo:     {fps.Value}");
        if (!string.IsNullOrWhiteSpace(quality)) Console.WriteLine($"  Qualidade:    {quality}");
        Console.WriteLine($"  Total:        {total}");
        Console.WriteLine($"  Sucesso:      {success}");
        Console.WriteLine($"  Ignorados:    {skipped}");
        if (dryRun) Console.WriteLine($"  Dry-Run:      {dryRuns}");
        Console.WriteLine($"  Falhas:       {failed}");
        Console.WriteLine(new string('═', 60));

        if (failed > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  DETALHES DAS FALHAS:");
            foreach (var r in results.Where(r => r.IsFailed))
            {
                Console.WriteLine($"    {r.DeviceName} ({r.DeviceId})");
                Console.WriteLine($"      {r.ErrorMessage}");
            }
        }

        if (!string.IsNullOrWhiteSpace(outputPath))
            WriteCsv(results, outputPath);

        Environment.ExitCode = failed > 0 ? 1 : 0;
    }

    private void WriteCsv(List<OperationResult> results, string path)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("DeviceId,DeviceName,OldFps,NewFps,Status,ErrorMessage");

            foreach (var r in results)
            {
                var oldFps = r.OldFps.HasValue ? r.OldFps.Value.ToString("F1") : "";
                var newFps = r.NewFps.HasValue ? r.NewFps.Value.ToString("F1") : "";
                var error = (r.ErrorMessage ?? "").Replace("\"", "\"\"");
                sb.AppendLine($"\"{r.DeviceId}\",\"{r.DeviceName}\",{oldFps},{newFps},{r.Status},\"{error}\"");
            }

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            _logger.LogSucesso($"Relatório CSV salvo em: {path}");
        }
        catch (Exception ex)
        {
            _logger.LogErro($"Falha ao escrever CSV: {ex.Message}");
        }
    }
}
