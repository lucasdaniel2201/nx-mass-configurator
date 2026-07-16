using System.Text.RegularExpressions;

namespace NxMassConfigurator.Services;

/// <summary>
/// MAC address normalization helper.
/// Port of normalize_mac() from app.py.
/// </summary>
public static class MacAddressHelper
{
    /// <summary>
    /// Normalize MAC to uppercase with dashes: E4-24-6C-99-7C-C4
    /// </summary>
    public static string Normalize(string mac)
    {
        if (string.IsNullOrWhiteSpace(mac))
            return mac?.Trim() ?? string.Empty;

        var clean = Regex.Replace(mac, @"[^0-9a-fA-F]", "");
        if (clean.Length != 12)
            return mac.Trim().ToUpperInvariant();

        var parts = new string[6];
        for (var i = 0; i < 6; i++)
            parts[i] = clean.Substring(i * 2, 2).ToUpperInvariant();

        return string.Join("-", parts);
    }
}
