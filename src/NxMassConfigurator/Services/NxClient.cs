using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NxMassConfigurator.Exceptions;
using NxMassConfigurator.Models;

namespace NxMassConfigurator.Services;

/// <summary>
/// Cliente REST para a API do NX Witness (Bearer Token).
/// Adaptado do projeto NxCameraCounter, com extensões para GET/PUT de device config.
/// </summary>
public class NxClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly ILogger _logger;
    private string? _primaryToken;
    private string? _secondaryToken;
    private bool _disposed;

    public string? ServerVersion { get; private set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public NxClient(string serverUrl, ILogger logger, bool allowInsecureSsl = true, int timeoutSeconds = 15)
    {
        _baseUrl = serverUrl.TrimEnd('/');
        _logger = logger;

        var handler = new HttpClientHandler();

        if (allowInsecureSsl)
        {
            handler.ServerCertificateCustomValidationCallback = AcceptAnyCertificate;
        }

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // ─── Authentication ───────────────────────────────────────────────

    public async Task AuthenticateAsync(string username, string password)
    {
        // Step 1: Verify user exists and is not cloud-only
        await CheckUserTypeAsync(username);

        // Step 2: Create payload
        var payload = new { username, password, setCookie = false };

        // Step 3: Get primary token
        var primaryResponse = await PostJsonAsync<LoginResponse>("/rest/v1/login/sessions", payload);
        _primaryToken = primaryResponse.Token;
        _logger.LogInfo($"Token primário obtido. Expira em {primaryResponse.ExpiresInS}s.");

        // Step 4: Get secondary token (for session cleanup)
        var secondaryResponse = await PostJsonAsync<LoginResponse>("/rest/v1/login/sessions", payload);
        _secondaryToken = secondaryResponse.Token;
        _logger.LogInfo("Token secundário obtido para logout.");

        // Step 5: Verify primary token is valid
        await GetJsonElementAsync($"/rest/v1/login/sessions/{_primaryToken}");
        _logger.LogSucesso("Autenticação validada com sucesso.");
    }

    // ─── Servers ──────────────────────────────────────────────────────

    public async Task<List<ServerInfo>> GetServersAsync()
    {
        var response = await GetJsonElementAsync("/rest/v1/servers/*/info");
        var servers = new List<ServerInfo>();

        if (response.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in response.EnumerateArray())
                servers.Add(ParseServerInfo(item));
        }
        else if (response.ValueKind == JsonValueKind.Object &&
                 response.TryGetProperty("reply", out var reply) &&
                 reply.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in reply.EnumerateArray())
                servers.Add(ParseServerInfo(item));
        }

        return servers;
    }

    // ─── Devices (list) ───────────────────────────────────────────────

    public async Task<List<JsonElement>> GetDevicesAsync(string? serverId = null)
    {
        var query = string.IsNullOrEmpty(serverId)
            ? "/rest/v1/devices"
            : $"/rest/v1/devices?serverId={Uri.EscapeDataString(serverId)}";

        var response = await GetJsonElementAsync(query);

        var deviceList = response;
        if (response.ValueKind == JsonValueKind.Object &&
            response.TryGetProperty("reply", out var replyArray) &&
            replyArray.ValueKind == JsonValueKind.Array)
        {
            deviceList = replyArray;
        }

        var devices = new List<JsonElement>();
        if (deviceList.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in deviceList.EnumerateArray())
                devices.Add(item);
        }

        return devices;
    }

    // ─── Device Config (single) ───────────────────────────────────────

    /// <summary>
    /// Obtém a configuração completa de um dispositivo via GET.
    /// Endpoint: GET /rest/v1/devices/{deviceId}
    /// </summary>
    public async Task<JsonElement> GetDeviceConfigAsync(string deviceId)
    {
        return await GetJsonElementAsync($"/rest/v1/devices/{CleanDeviceId(deviceId)}");
    }

    /// <summary>
    /// Atualiza a configuração de um dispositivo via PUT.
    /// Endpoint: PUT /rest/v1/devices/{deviceId}
    /// Envia o JSON completo (read-modify-write).
    /// </summary>
    public async Task<JsonElement> UpdateDeviceAsync(string deviceId, string jsonBody)
    {
        var path = $"/rest/v1/devices/{CleanDeviceId(deviceId)}";
        var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        var response = await SendAsync(HttpMethod.Put, path, content);
        var responseJson = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(responseJson, JsonOptions);
    }

    /// <summary>
    /// Remove chaves { } do ID do device para uso em URLs (NX Witness espera ID limpo).
    /// </summary>
    private static string CleanDeviceId(string id)
        => id.Trim('{', '}').Trim();

    // ─── Logout ───────────────────────────────────────────────────────

    public async Task LogoutAsync()
    {
        if (_secondaryToken != null)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Delete,
                    $"{_baseUrl}/rest/v1/login/sessions/{_secondaryToken}");
                AddAuthHeader(request);
                await _httpClient.SendAsync(request);
                _logger.LogInfo("Sessão encerrada (logout).");
            }
            catch (Exception ex)
            {
                _logger.LogWarn($"Logout ignorado: {ex.Message}");
            }
        }
        _primaryToken = null;
        _secondaryToken = null;
    }

    // ─── Private: HTTP send pipeline ──────────────────────────────────

    private async Task CheckUserTypeAsync(string username)
    {
        try
        {
            var response = await GetJsonElementAsync(
                $"/rest/v1/login/users/{Uri.EscapeDataString(username)}");

            var userType = GetStringProperty(response, "type");
            if (userType == "cloud")
            {
                throw new NxAuthException(
                    "Usuários Cloud não são suportados. Use um usuário local ou LDAP.");
            }
        }
        catch (NxAuthException)
        {
            throw;
        }
        catch
        {
            // endpoint pode não existir em versões antigas
        }
    }

    private async Task<T> GetJsonAsync<T>(string path) where T : class
    {
        var response = await SendAsync(HttpMethod.Get, path);
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
               ?? throw new NxApiException("Resposta vazia do servidor.");
    }

    private async Task<JsonElement> GetJsonElementAsync(string path)
    {
        var response = await SendAsync(HttpMethod.Get, path);
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(json, JsonOptions);
    }

    private async Task<T> PostJsonAsync<T>(string path, object payload) where T : class
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await SendAsync(HttpMethod.Post, path, content);
        var responseJson = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(responseJson, JsonOptions)
               ?? throw new NxApiException("Resposta vazia do servidor.");
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, HttpContent? content = null)
    {
        var url = $"{_baseUrl}{path}";
        var request = new HttpRequestMessage(method, url) { Content = content };
        AddAuthHeader(request);

        try
        {
            var response = await _httpClient.SendAsync(request);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new NxRateLimitException(
                    $"Rate limit (429) em {path}. Aguarde antes de reenviar.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                throw new NxApiException(
                    $"Erro HTTP {(int)response.StatusCode} em {path}: {Truncate(body, 200)}",
                    (int)response.StatusCode,
                    path);
            }

            return response;
        }
        catch (NxApiException) { throw; }
        catch (TaskCanceledException)
        {
            throw new NxTimeoutException(
                $"Timeout ao conectar em {_baseUrl}. Servidor não respondeu a tempo.");
        }
        catch (HttpRequestException ex)
        {
            throw new NxConnectionException(
                $"Falha de conexão com {_baseUrl}: {ex.Message}", ex);
        }
    }

    private void AddAuthHeader(HttpRequestMessage request)
    {
        if (_primaryToken != null)
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", _primaryToken);
        }
    }

    // ─── Parsing helpers ──────────────────────────────────────────────

    private static ServerInfo ParseServerInfo(JsonElement item)
    {
        var info = new ServerInfo
        {
            Id = GetStringProperty(item, "id"),
            Name = GetStringProperty(item, "name"),
            Url = GetStringProperty(item, "url"),
            Status = GetStringProperty(item, "status"),
            Version = GetStringProperty(item, "version")
        };
        return info;
    }

    // ─── Static utility ───────────────────────────────────────────────

    public static string GetStringProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var prop) &&
            prop.ValueKind == JsonValueKind.String)
        {
            var value = prop.GetString();
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value;
        }
        return string.Empty;
    }

    public static long GetLongProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var prop) &&
            prop.ValueKind == JsonValueKind.Number)
        {
            return prop.GetInt64();
        }
        return 0;
    }

    public static double? GetDoubleProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var prop) &&
            prop.ValueKind == JsonValueKind.Number)
        {
            return prop.GetDouble();
        }
        return null;
    }

    private static bool AcceptAnyCertificate(
        HttpRequestMessage msg, X509Certificate2? cert, X509Chain? chain,
        SslPolicyErrors errors) => true;

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";

    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient.Dispose();
            _disposed = true;
        }
    }
}
