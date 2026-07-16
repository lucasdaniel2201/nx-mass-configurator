using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using NxMassConfigurator.Models.Qnap;

namespace NxMassConfigurator.Services.Qnap;

/// <summary>
/// Manages a QNAP FileStation API session (login → SID → operations → logout).
/// Disposable: always calls logout in Dispose/using blocks.
/// </summary>
public sealed class QnapSession : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private string? _sid;
    private bool _disposed;

    public string ServerIp { get; }
    public bool TlsVerify { get; }

    private static bool AllowAnyCertificate(
        HttpRequestMessage msg, X509Certificate2? cert, X509Chain? chain,
        SslPolicyErrors errors) => true;

    public QnapSession(string serverIp, bool tlsVerify = true)
    {
        ServerIp = serverIp;
        TlsVerify = tlsVerify;

        var handler = new HttpClientHandler();
        if (!tlsVerify)
        {
            handler.ServerCertificateCustomValidationCallback = AllowAnyCertificate;
        }

        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        _baseUrl = $"https://{serverIp}";
    }

    /// <summary>
    /// Authenticate and get a SID token.
    /// </summary>
    public async Task<string> LoginAsync(string username, string password)
    {
        var effectiveUser = string.IsNullOrWhiteSpace(username) ? "Guest" : username.Trim();
        var effectivePass = password ?? string.Empty;
        var encodedPwd = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(effectivePass));

        HttpResponseMessage authResp;
        try
        {
            authResp = await _httpClient.GetAsync(
                $"{_baseUrl}/cgi-bin/authLogin.cgi?user={Uri.EscapeDataString(effectiveUser)}&pwd={Uri.EscapeDataString(encodedPwd)}");
        }
        catch (HttpRequestException ex)
        {
            var innerMsg = ex.InnerException?.Message ?? ex.Message;
            var hint = innerMsg.Contains("SSL") || innerMsg.Contains("certificate") || innerMsg.Contains("ssl")
                ? " (certificado inválido/autoassinado — desmarque TLS)"
                : "";
            throw new QnapApiPreSendException(
                $"QNAP indisponível antes da autenticação em {ServerIp}: {innerMsg}{hint}", ex);
        }

        var body = await authResp.Content.ReadAsStringAsync();
        var sidMatch = Regex.Match(body,
            @"<authSid><!\[CDATA\[(.+?)\]\]></authSid>",
            RegexOptions.None, TimeSpan.FromSeconds(5));

        if (!sidMatch.Success)
            throw new QnapApiPreSendException(
                $"QNAP API: Falha na autenticação no {ServerIp}. Resposta: {Truncate(body, 200)}");

        _sid = sidMatch.Groups[1].Value;
        return _sid;
    }

    /// <summary>
    /// List all entries in a FileStation directory.
    /// </summary>
    public async Task<List<QnapFileEntry>> ListFolderAsync(string share, string relPath)
    {
        EnsureAuthenticated();

        var path = $"/{share.Trim('/')}";
        if (!string.IsNullOrWhiteSpace(relPath))
            path += $"/{relPath.Trim('/')}";

        var listResp = await _httpClient.GetAsync(
            $"{_baseUrl}/cgi-bin/filemanager/utilRequest.cgi" +
            $"?func=get_list&sid={_sid}&path={Uri.EscapeDataString(path)}&limit=1000&start=0&sort=filename&dir=ASC");

        var json = await listResp.Content.ReadAsStringAsync();

        // Parse simple JSON manually (avoid heavy JsonNode overhead per call)
        return QnapJsonParser.ParseFileList(json);
    }

    /// <summary>
    /// Delete a folder via FileStation API.
    /// </summary>
    public async Task DeleteFolderAsync(string share, string relPath)
    {
        EnsureAuthenticated();

        // Split relative path into parent path and folder name
        // e.g. "UUID/hi_quality/MAC" → parent = "/share/UUID/hi_quality", name = "MAC"
        var cleanParts = relPath.Replace('\\', '/').Trim('/').Split('/');
        if (cleanParts.Length == 0)
            throw new QnapAmbiguousException("Caminho de exclusão vazio.");

        var folderName = cleanParts[^1];
        var parentPath = $"/{share.Trim('/')}";
        if (cleanParts.Length > 1)
            parentPath += "/" + string.Join("/", cleanParts.Take(cleanParts.Length - 1));

        HttpResponseMessage delResp;
        try
        {
            delResp = await _httpClient.GetAsync(
                $"{_baseUrl}/cgi-bin/filemanager/utilRequest.cgi" +
                $"?func=delete&sid={_sid}&path={Uri.EscapeDataString(parentPath)}" +
                $"&file_name={Uri.EscapeDataString(folderName)}&file_total=1");
        }
        catch (HttpRequestException ex)
        {
            throw new QnapAmbiguousException(
                "A exclusão via API teve resultado ambíguo; não será repetida via SMB.", ex);
        }

        if (delResp.StatusCode != HttpStatusCode.OK)
            throw new QnapAmbiguousException(
                $"QNAP API delete HTTP {(int)delResp.StatusCode}: {Truncate(await delResp.Content.ReadAsStringAsync(), 300)}");

        var responseText = await delResp.Content.ReadAsStringAsync();

        // Parse the response: success == "true" OR status == 1
        var (success, status) = QnapJsonParser.ParseDeleteResult(responseText);
        if (success != "true" && status != 1)
            throw new QnapAmbiguousException(
                $"QNAP API delete falhou: {Truncate(responseText, 300)}");
    }

    /// <summary>
    /// Logout (idempotent, safe to call multiple times).
    /// </summary>
    public async Task LogoutAsync()
    {
        if (_sid == null) return;
        try
        {
            await _httpClient.GetAsync(
                $"{_baseUrl}/cgi-bin/authLogout.cgi?sid={_sid}");
        }
        catch
        {
            // Logout is best-effort
        }
        _sid = null;
    }

    private void EnsureAuthenticated()
    {
        if (string.IsNullOrEmpty(_sid))
            throw new QnapApiPreSendException("QNAP session not authenticated.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            LogoutAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // best-effort on dispose
        }

        _httpClient.Dispose();
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";
}

/// <summary>
/// Represents a file or folder entry in QNAP FileStation.
/// </summary>
public sealed class QnapFileEntry
{
    public string FileName { get; init; } = string.Empty;
    public bool IsFolder { get; init; }

    public override string ToString() => FileName;
}
