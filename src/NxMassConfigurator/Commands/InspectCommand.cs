using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using NxMassConfigurator.Exceptions;
using NxMassConfigurator.Services;

namespace NxMassConfigurator.Commands;

/// <summary>
/// Comando de inspeção: obtém a configuração completa de um device via GET
/// e exibe o JSON formatado (console ou arquivo).
/// </summary>
public class InspectCommand
{
    private static readonly Option<string> UrlOption = new(
        ["--url", "-u"],
        getDefaultValue: () => "https://10.81.5.11:7001",
        description: "URL base do servidor NX Witness.");

    private static readonly Option<string> UserOption = new(
        ["--user", "-n"],
        "Nome de usuário do NX Witness")
    { IsRequired = true };

    private static readonly Option<string?> PasswordOption = new(
        ["--password", "-p"],
        "Senha do usuário. Se omitido, será solicitado de forma interativa.");

    private static readonly Option<string> ServerOption = new(
        ["--server", "-s"],
        "Nome ou UUID do servidor alvo")
    { IsRequired = true };

    private static readonly Option<string?> DeviceOption = new(
        ["--device", "-d"],
        "Nome ou UUID de uma câmera específica. Se omitido, mostra a primeira do servidor.");

    private static readonly Option<string?> OutputOption = new(
        ["--output", "-o"],
        "Caminho para salvar o JSON. Se omitido, exibe no console.");

    private static readonly Option<bool> InsecureSslOption = new(
        ["--insecure-ssl"],
        getDefaultValue: () => true,
        "Ignorar validação de certificado SSL.");

    private static readonly Option<int> TimeoutOption = new(
        ["--timeout"],
        getDefaultValue: () => 15,
        "Timeout HTTP em segundos.");

    public static Command Create()
    {
        var command = new Command("inspect",
            "Obtém e exibe a configuração completa (JSON) de uma câmera NX Witness.")
        {
            UrlOption, UserOption, PasswordOption, ServerOption, DeviceOption,
            OutputOption, InsecureSslOption, TimeoutOption
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
        var output = ctx.ParseResult.GetValueForOption(OutputOption);
        var insecureSsl = ctx.ParseResult.GetValueForOption(InsecureSslOption);
        var timeout = ctx.ParseResult.GetValueForOption(TimeoutOption);

        var logger = new ConsoleLogger(verbose: true);

        try
        {
            if (string.IsNullOrWhiteSpace(password))
                password = PromptPassword("Digite a senha do NX Witness: ");

            if (string.IsNullOrWhiteSpace(password))
            {
                logger.LogErro("Senha não fornecida. Abortando.");
                Environment.ExitCode = 2;
                return;
            }

            using var client = new NxClient(url, logger, insecureSsl, timeout);

            logger.LogInfo($"Autenticando como '{user}' em {url}...");
            await client.AuthenticateAsync(user, password);

            logger.LogInfo("Descobrindo servidores...");
            var servers = await client.GetServersAsync();
            var targetServer = servers.FirstOrDefault(s =>
                string.Equals(s.Id, server, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Name, server, StringComparison.OrdinalIgnoreCase));

            if (targetServer == null)
            {
                logger.LogErro($"Servidor '{server}' não encontrado.");
                foreach (var s in servers)
                    Console.WriteLine($"  - {s.Name} (id={s.Id})");
                await client.LogoutAsync();
                Environment.ExitCode = 2;
                return;
            }

            logger.LogSucesso($"Servidor: {targetServer.Name} (v{targetServer.Version})");

            logger.LogInfo("Listando câmeras...");
            var allDevices = await client.GetDevicesAsync(targetServer.Id);

            if (allDevices.Count == 0)
            {
                logger.LogErro("Nenhuma câmera encontrada.");
                await client.LogoutAsync();
                Environment.ExitCode = 2;
                return;
            }

            JsonElement targetDevice;
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
                    logger.LogErro($"Câmera '{deviceFilter}' não encontrada.");
                    logger.LogInfo("Disponíveis:");
                    foreach (var d in allDevices)
                        Console.WriteLine($"  - {NxClient.GetStringProperty(d, "name")} (id={NxClient.GetStringProperty(d, "id")})");
                    await client.LogoutAsync();
                    Environment.ExitCode = 2;
                    return;
                }

                targetDevice = match;
            }
            else
            {
                targetDevice = allDevices[0];
                logger.LogInfo("Nenhum --device especificado. Usando primeira câmera do servidor.");
            }

            var deviceId = NxClient.GetStringProperty(targetDevice, "id");
            var deviceName = NxClient.GetStringProperty(targetDevice, "name");
            logger.LogInfo($"Obtendo config completa de '{deviceName}' (id={deviceId})...");

            var fullConfig = await client.GetDeviceConfigAsync(deviceId);

            // Pretty-print JSON
            var prettyJson = JsonSerializer.Serialize(fullConfig, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            if (!string.IsNullOrWhiteSpace(output))
            {
                File.WriteAllText(output, prettyJson);
                logger.LogSucesso($"JSON salvo em: {output}");
                logger.LogInfo($"Tamanho: {prettyJson.Length} caracteres.");
            }
            else
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine(prettyJson);
                Console.ResetColor();
                Console.WriteLine();
                logger.LogInfo($"JSON com {prettyJson.Length} caracteres exibido acima.");
            }

            await client.LogoutAsync();
        }
        catch (NxApiException ex)
        {
            logger.LogErro($"Erro da API: {ex.Message}");
            Environment.ExitCode = 2;
        }
        catch (Exception ex)
        {
            logger.LogErro($"Erro fatal: {ex.GetType().Name}: {ex.Message}");
            Environment.ExitCode = 2;
        }
    }

    private static string? PromptPassword(string prompt)
    {
        Console.Write(prompt);
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (key.Key == ConsoleKey.Escape) { Console.WriteLine(" (cancelado)"); return null; }
            sb.Append(key.KeyChar);
            Console.Write("*");
        }
        return sb.ToString();
    }
}
