using System.CommandLine;
using NxMassConfigurator.Commands;
using NxMassConfigurator.Services;

namespace NxMassConfigurator;

public class Program
{
    // Configuração do repositório GitHub (ajuste ANTES de publicar)
    private const string RepoOwner = "lucasdaniel2201";
    private const string RepoName = "nx-mass-configurator";

    public static async Task<int> Main(string[] args)
    {
        Console.CancelKeyPress += (_, _) =>
        {
            Console.ResetColor();
            Console.WriteLine("\n[INTERROMPIDO] Operação cancelada pelo usuário.");
            Environment.Exit(2);
        };

        var logger = new ConsoleLogger(verbose: true);
        var updater = new AutoUpdater(logger, RepoOwner, RepoName);

        // ── Flags globais ──
        bool noUpdate = args.Any(a => a is "--no-update" or "-nu");
        bool checkUpdate = args.Any(a => a is "--check-update" or "-cu");

        // ── Check-update explícito ──
        if (checkUpdate)
        {
            logger.LogInfo("Modo check-update: verificando GitHub Releases...");
            await updater.CheckAndReportAsync();
            return 0;
        }

        // ── Background silent update check ──
        Task? updateTask = null;
        if (!noUpdate)
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            updateTask = Task.Run(async () =>
            {
                try
                {
                    await updater.CheckAndDownloadAsync();
                }
                catch
                {
                    // Silencioso — falha no update não interrompe o programa
                }
            }, cts.Token);
        }

        // ── CLI principal ──
        var rootCommand = new RootCommand(
            "NX Mass Configurator v1.4 — Altera configs de câmeras NX Witness. " +
            "Flags globais: --check-update, --no-update");

        rootCommand.Add(ApplyConfigCommand.Create());
        rootCommand.Add(InspectCommand.Create());

        int exitCode = await rootCommand.InvokeAsync(args.Where(a =>
            a is not "--check-update" and not "-cu" and not "--no-update" and not "-nu").ToArray());

        // ── Aguarda background update e aplica ──
        if (updateTask != null)
        {
            try { await updateTask; } catch { /* silencioso */ }
        }

        // Aplica update pendente (substitui .exe ao encerrar)
        updater.ApplyPendingUpdate();

        return exitCode;
    }
}
