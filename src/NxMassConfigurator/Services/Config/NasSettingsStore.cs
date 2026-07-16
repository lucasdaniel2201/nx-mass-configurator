using System.Security.Cryptography;
using System.Text.Json;
using NxMassConfigurator.Models.Qnap;

namespace NxMassConfigurator.Services.Config;

/// <summary>
/// Stores and retrieves NAS settings (IPs, share, username, encrypted password)
/// from a JSON file in %APPDATA%. Passwords are encrypted with Windows DPAPI.
/// </summary>
public static class NasSettingsStore
{
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HOWBE", "nx-mass-config-wpf");

    private static readonly string SettingsPath = Path.Combine(AppDataDir, "nas-settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private const DataProtectionScope DpapiScope = DataProtectionScope.CurrentUser;

    /// <summary>
    /// Load settings, creating defaults if the file does not exist.
    /// </summary>
    public static NasSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<NasSettings>(json, JsonOptions);
                if (settings != null)
                    return settings;
            }
        }
        catch
        {
            // Corrupt settings? Return defaults.
        }

        return CreateDefaults();
    }

    /// <summary>
    /// Save settings (passwords are already encrypted at this point — raw PlainTextPassword is not persisted).
    /// </summary>
    public static void Save(NasSettings settings)
    {
        Directory.CreateDirectory(AppDataDir);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsPath, json);
    }

    /// <summary>
    /// Encrypt a plain-text password for storage.
    /// </summary>
    public static string? ProtectPassword(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return null;

        var plainBytes = System.Text.Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = ProtectedData.Protect(plainBytes, null, DpapiScope);
        return Convert.ToBase64String(cipherBytes);
    }

    /// <summary>
    /// Decrypt a stored password back to plain text.
    /// </summary>
    public static string? UnprotectPassword(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64))
            return null;

        try
        {
            var cipherBytes = Convert.FromBase64String(protectedBase64);
            var plainBytes = ProtectedData.Unprotect(cipherBytes, null, DpapiScope);
            return System.Text.Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return null; // Corrupted or encrypted by another user
        }
    }

    private static NasSettings CreateDefaults()
    {
        return new NasSettings
        {
            QnapShare = "Public",
            QnapUsername = "admin",
            QnapPasswordProtected = null,
            QnapTlsVerify = false,  // default false: QNAPs locais usam certificado autoassinado
            Subfolders = new List<string> { "hi_quality", "low_quality" },
            Servers = new List<NasServerConfig>
            {
                new() { Ip = "10.81.5.14", Share = "Public", Selected = true },
                new() { Ip = "10.81.5.15", Share = "Public", Selected = true },
                new() { Ip = "10.81.5.16", Share = "Public", Selected = true },
                new() { Ip = "10.81.5.17", Share = "Public", Selected = true },
            }
        };
    }
}

/// <summary>
/// Full NAS configuration persisted to disk.
/// </summary>
public class NasSettings
{
    public string QnapShare { get; set; } = "Public";
    public string QnapUsername { get; set; } = string.Empty;
    public string? QnapPasswordProtected { get; set; }
    public bool QnapTlsVerify { get; set; } = true;
    public List<string> Subfolders { get; set; } = new() { "hi_quality", "low_quality" };
    public List<NasServerConfig> Servers { get; set; } = new();
}
