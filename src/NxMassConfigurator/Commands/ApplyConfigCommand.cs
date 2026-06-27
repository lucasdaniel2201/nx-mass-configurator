using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using NxMassConfigurator.Exceptions;
using NxMassConfigurator.Services;

namespace NxMassConfigurator.Commands;

/// <summary>
/// Comando unificado: altera FPS e/ou qualidade de gravação em câmeras de um servidor NX Witness.
/// Pelo menos --fps ou --quality deve ser informado.
/// </summary>
public class ApplyConfigCommand
{
    private const string DefaultUrl = "https://10.81.5.11:7001";

    private static readonly Option<string> UrlOption = new(
        ["--url", "-u"],
        getDefaultValue: () => DefaultUrl,
        description: $"URL base do servidor NX Witness. Padrão: {DefaultUrl}");

    private static readonly Option<string> UserOption = new(
        ["--user", "-n"],
        "Nome de usuário do NX Witness (local ou LDAP)")
    { IsRequired = true };

    private static readonly Option<string?> PasswordOption = new(
        ["--password", "-p"],
        "Senha do usuário. Se omitido, solicitado interativamente (mascarado).");

    private static readonly Option<string> ServerOption = new(
        ["--server", "-s"],
        "Nome ou UUID do servidor alvo")
    { IsRequired = true };

    private static readonly Option<string?> DeviceOption = new(
        ["--device", "-d"],
        "Nome ou UUID de uma câmera específica. Se omitido, processa TODAS.");

    private static readonly Option<double?> FpsOption = new(
        ["--fps"],
        description: "Novo valor de FPS a ser aplicado. Se omitido, não altera FPS.");

    private static readonly Option<string?> QualityOption = new(
        ["--quality", "-q"],
        description: "Qualidade de gravação: low, medium, high. Se omitido, não altera.");

    private static readonly Option<bool> DryRunOption = new(
        ["--dry-run"],
        getDefaultValue: () => false,
        "Simular alterações sem aplicar (GET + diff, sem PUT/PATCH)");

    private static readonly Option<int> ConcurrencyOption = new(
        ["--concurrency", "-c"],
        getDefaultValue: () => 6,
        "Requisições simultâneas. Padrão: 6");

    private static readonly Option<string?> OutputOption = new(
        ["--output", "-o"],
        "Caminho para CSV de relatório. Se omitido, apenas console.");

    private static readonly Option<bool> InsecureSslOption = new(
        ["--insecure-ssl"],
        getDefaultValue: () => true,
        "Ignorar validação SSL (cert autoassinado). Padrão: true");

    private static readonly Option<int> TimeoutOption = new(
        ["--timeout"],
        getDefaultValue: () => 15,
        "Timeout HTTP em segundos. Padrão: 15");

    private static readonly Option<bool> UnlockStreamOption = new(
        ["--unlock-stream"],
        getDefaultValue: () => false,
        "Desmarca 'Manter o stream da câmera e as configurações do perfil' (NX passa a gerenciar).");

    private static readonly Option<bool> LockStreamOption = new(
        ["--lock-stream"],
        getDefaultValue: () => false,
        "Marca 'Manter o stream da câmera e as configurações do perfil' (câmera mantém próprias configs).");

    private static readonly Option<bool> EnableAudioOption = new(
        ["--enable-audio"],
        getDefaultValue: () => false,
        "Desmarca 'Do not record audio' (permite gravar áudio).");

    private static readonly Option<bool> DisableAudioOption = new(
        ["--disable-audio"],
        getDefaultValue: () => false,
        "Marca 'Do not record audio' (não grava áudio).");

    private static readonly Option<bool> EnableSecondaryOption = new(
        ["--enable-secondary"],
        getDefaultValue: () => false,
        "Desmarca 'Do not record secondary stream' (permite gravar stream secundário).");

    private static readonly Option<bool> DisableSecondaryOption = new(
        ["--disable-secondary"],
        getDefaultValue: () => false,
        "Marca 'Do not record secondary stream' (não grava stream secundário).");

    private static readonly Option<string?> CodecOption = new(
        ["--codec"],
        description: "Codec alvo para o stream primário (ex: H265, H264). Se omitido, não altera.");

    private static readonly Option<string?> ResolutionOption = new(
        ["--resolution"],
        description: "Resolução alvo para o stream primário (ex: 1920x1080, 1280x720). Se omitido, não altera.");

    public static Command Create()
    {
        var command = new Command("apply",
            "Altera FPS, qualidade, stream, áudio, secundário, codec e resolução em câmeras NX Witness.")
        {
            UrlOption, UserOption, PasswordOption, ServerOption, DeviceOption,
            FpsOption, QualityOption,
            UnlockStreamOption, LockStreamOption,
            EnableAudioOption, DisableAudioOption,
            EnableSecondaryOption, DisableSecondaryOption,
            CodecOption, ResolutionOption,
            DryRunOption, ConcurrencyOption, OutputOption,
            InsecureSslOption, TimeoutOption
        };

        command.SetHandler(RunAsync);
        return command;
    }

    private static async Task RunAsync(InvocationContext ctx)
    {
        var url = ctx.ParseResult.GetValueForOption(UrlOption)!;
        var user = ctx.ParseResult.GetValueForOption(UserOption)!;
        var password = ctx.ParseResult.GetValueForOption(PasswordOption);
        var server = ctx.ParseResult.GetValueForOption(ServerOption)!;
        var deviceFilter = ctx.ParseResult.GetValueForOption(DeviceOption);
        var fps = ctx.ParseResult.GetValueForOption(FpsOption);
        var quality = ctx.ParseResult.GetValueForOption(QualityOption);
        var dryRun = ctx.ParseResult.GetValueForOption(DryRunOption);
        var concurrency = ctx.ParseResult.GetValueForOption(ConcurrencyOption);
        var output = ctx.ParseResult.GetValueForOption(OutputOption);
        var insecureSsl = ctx.ParseResult.GetValueForOption(InsecureSslOption);
        var timeout = ctx.ParseResult.GetValueForOption(TimeoutOption);
        var unlockStream = ctx.ParseResult.GetValueForOption(UnlockStreamOption);
        var lockStream = ctx.ParseResult.GetValueForOption(LockStreamOption);
        var enableAudio = ctx.ParseResult.GetValueForOption(EnableAudioOption);
        var disableAudio = ctx.ParseResult.GetValueForOption(DisableAudioOption);
        var enableSecondary = ctx.ParseResult.GetValueForOption(EnableSecondaryOption);
        var disableSecondary = ctx.ParseResult.GetValueForOption(DisableSecondaryOption);
        var codec = ctx.ParseResult.GetValueForOption(CodecOption);
        var resolution = ctx.ParseResult.GetValueForOption(ResolutionOption);

        var logger = new ConsoleLogger(verbose: true);

        try
        {
            // Validate: at least one action must be specified
            if (!fps.HasValue && string.IsNullOrWhiteSpace(quality) && !unlockStream && !lockStream
                && !enableAudio && !disableAudio && !enableSecondary && !disableSecondary
                && string.IsNullOrWhiteSpace(codec) && string.IsNullOrWhiteSpace(resolution))
            {
                logger.LogErro("Informe ao menos uma ação: --fps, --quality, --unlock/lock-stream, --enable/disable-audio, --enable/disable-secondary, --codec, --resolution.");
                Environment.ExitCode = 2;
                return;
            }

            if (string.IsNullOrWhiteSpace(password))
                password = PromptPassword("Digite a senha do NX Witness: ");

            if (string.IsNullOrWhiteSpace(password))
            {
                logger.LogErro("Senha não fornecida. Abortando.");
                Environment.ExitCode = 2;
                return;
            }

            PrintBanner(logger, url, user, server, deviceFilter, fps, quality, unlockStream, lockStream,
                enableAudio, disableAudio, enableSecondary, disableSecondary, codec, resolution, dryRun);

            using var client = new NxClient(url, logger, insecureSsl, timeout);

            logger.LogInfo($"Autenticando como '{user}' em {url}...");
            try
            {
                await client.AuthenticateAsync(user, password);
            }
            catch (NxAuthException ex)
            {
                logger.LogErro($"Falha na autenticação: {ex.Message}");
                Environment.ExitCode = 2;
                return;
            }

            logger.LogInfo("Descobrindo servidores do sistema...");
            var servers = await client.GetServersAsync();

            var targetServer = servers.FirstOrDefault(s =>
                string.Equals(s.Id, server, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Name, server, StringComparison.OrdinalIgnoreCase));

            if (targetServer == null)
            {
                logger.LogErro($"Servidor '{server}' não encontrado. Disponíveis:");
                foreach (var s in servers)
                    Console.WriteLine($"  - {s.Name} (id={s.Id}, status={s.Status}, v{s.Version})");
                await client.LogoutAsync();
                Environment.ExitCode = 2;
                return;
            }

            logger.LogSucesso($"Servidor: {targetServer.Name} (v{targetServer.Version})");
            logger.LogInfo($"ID: {targetServer.Id}");

            logger.LogInfo("Listando câmeras do servidor...");
            var allDevices = await client.GetDevicesAsync(targetServer.Id);

            if (allDevices.Count == 0)
            {
                logger.LogErro($"Nenhuma câmera em '{targetServer.Name}'.");
                await client.LogoutAsync();
                Environment.ExitCode = 2;
                return;
            }

            logger.LogInfo($"{allDevices.Count} câmeras encontradas.");

            List<JsonElement> devices;
            if (!string.IsNullOrWhiteSpace(deviceFilter))
            {
                var match = allDevices.FirstOrDefault(d =>
                {
                    var id = NxClient.GetStringProperty(d, "id");
                    var name = NxClient.GetStringProperty(d, "name");
                    return string.Equals(id, deviceFilter, StringComparison.OrdinalIgnoreCase) ||
                           name.Contains(deviceFilter, StringComparison.OrdinalIgnoreCase);
                });

                if (match.ValueKind == JsonValueKind.Undefined)
                {
                    logger.LogErro($"Câmera '{deviceFilter}' não encontrada. Disponíveis:");
                    foreach (var d in allDevices)
                        Console.WriteLine($"  - {NxClient.GetStringProperty(d, "name")} (id={NxClient.GetStringProperty(d, "id")})");
                    await client.LogoutAsync();
                    Environment.ExitCode = 2;
                    return;
                }

                devices = new List<JsonElement> { match };
                logger.LogInfo($"Filtro: apenas '{NxClient.GetStringProperty(match, "name")}'.");
            }
            else
            {
                devices = allDevices;
            }

            // Process
            var updater = new NxConfigUpdater(client, logger);
            var processor = new DeviceProcessor(updater, logger, concurrency, maxRetries: 3);
            var results = await processor.ProcessAllAsync(devices, targetServer.Name, fps, quality,
                unlockStream, lockStream, enableAudio, disableAudio, enableSecondary, disableSecondary,
                codec, resolution, dryRun);

            // Report
            var reportWriter = new ReportWriter(logger);
            reportWriter.Write(results, output, targetServer.Name, fps, quality, dryRun);

            await client.LogoutAsync();
        }
        catch (NxConnectionException ex)
        {
            logger.LogErro($"Falha de conexão: {ex.Message}");
            Environment.ExitCode = 2;
        }
        catch (NxTimeoutException ex)
        {
            logger.LogErro($"Timeout: {ex.Message}");
            Environment.ExitCode = 2;
        }
        catch (Exception ex)
        {
            logger.LogErro($"Erro fatal: {ex.GetType().Name}: {ex.Message}");
            Environment.ExitCode = 2;
        }
    }

    private static void PrintBanner(ILogger logger, string url, string user, string server,
        string? deviceFilter, double? fps, string? quality, bool unlockStream, bool lockStream,
        bool enableAudio, bool disableAudio, bool enableSecondary, bool disableSecondary,
        string? codec, string? resolution, bool dryRun)
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
        Console.WriteLine("║     NX Mass Configurator v1.4                           ║");
        Console.WriteLine("║     Configuração completa de câmeras NX Witness         ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
        Console.ResetColor();

        logger.LogInfo($"URL:       {url}");
        logger.LogInfo($"Usuário:   {user}");
        logger.LogInfo($"Servidor:  {server}");
        logger.LogInfo($"Câmera:    {(string.IsNullOrWhiteSpace(deviceFilter) ? "TODAS" : deviceFilter)}");
        logger.LogInfo($"FPS alvo:  {(fps.HasValue ? fps.Value.ToString() : "—")}");
        logger.LogInfo($"Qualidade: {(string.IsNullOrWhiteSpace(quality) ? "—" : quality)}");
        if (unlockStream)    logger.LogInfo($"Stream:    DESMARCAR");
        if (lockStream)      logger.LogInfo($"Stream:    MARCAR");
        if (enableAudio)     logger.LogInfo($"Áudio:     HABILITAR");
        if (disableAudio)    logger.LogInfo($"Áudio:     DESABILITAR");
        if (enableSecondary) logger.LogInfo($"Secundário: HABILITAR");
        if (disableSecondary)logger.LogInfo($"Secundário: DESABILITAR");
        if (!string.IsNullOrWhiteSpace(codec))       logger.LogInfo($"Codec:     {codec}");
        if (!string.IsNullOrWhiteSpace(resolution))  logger.LogInfo($"Resolução: {resolution}");
        logger.LogInfo($"Modo:      {(dryRun ? "DRY-RUN (simulação)" : "EXECUÇÃO REAL")}");
        Console.WriteLine();
    }

    private static string? PromptPassword(string prompt)
    {
        Console.Write(prompt);
        var password = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
            if (key.Key == ConsoleKey.Backspace) { if (password.Length > 0) password.Length--; continue; }
            if (key.Key == ConsoleKey.Escape) { Console.WriteLine(" (cancelado)"); return null; }
            password.Append(key.KeyChar);
            Console.Write("*");
        }
        return password.ToString();
    }
}
