using NxMassConfigurator.Models.Qnap;

namespace NxMassConfigurator.Services.Qnap;

/// <summary>
/// Search service: finds MAC folders on QNAP NAS via FileStation API.
/// </summary>
public static class QnapSearchService
{
    /// <summary>
    /// Search for a MAC folder via QNAP FileStation HTTP API (fast).
    /// Algorithm: list UUID folders → check subfolder → look for target MAC folder.
    /// </summary>
    public static async Task<List<MacFoundPath>> SearchViaApiAsync(
        string serverIp,
        string share,
        string username,
        string password,
        string targetMacFolder,
        string subfolder,
        bool tlsVerify = true,
        IProgress<(int, string)>? progress = null,
        CancellationToken ct = default)
    {
        var found = new List<MacFoundPath>();
        var targetUpper = targetMacFolder.ToUpperInvariant();
        var subfolderTrimmed = subfolder.Trim();
        var dirsScanned = 0;

        using var session = new QnapSession(serverIp, tlsVerify);
        await session.LoginAsync(username, password);

        try
        {
            // Step 1: List all top-level directories in /share
            var topEntries = await session.ListFolderAsync(share, "");
            var topDirs = topEntries
                .Where(e => e.IsFolder
                    && !e.FileName.StartsWith("@")
                    && !e.FileName.StartsWith("#"))
                .Select(e => e.FileName)
                .ToList();

            // Step 2: For each UUID folder, check subfolder for the MAC target
            foreach (var top in topDirs)
            {
                ct.ThrowIfCancellationRequested();
                dirsScanned++;
                progress?.Report((dirsScanned, top));

                var subPath = $"{top}/{subfolderTrimmed}";

                try
                {
                    var subEntries = await session.ListFolderAsync(share, subPath);
                    foreach (var entry in subEntries)
                    {
                        if (entry.IsFolder && entry.FileName.ToUpperInvariant() == targetUpper)
                        {
                            found.Add(new MacFoundPath
                            {
                                ServerIp = serverIp,
                                Share = share,
                                UuidFolder = top,
                                Subfolder = subfolderTrimmed,
                                MacFolder = entry.FileName,
                                Method = "api"
                            });
                        }
                    }
                }
                catch
                {
                    // Directory might not exist or be inaccessible — skip and continue
                }
            }
        }
        finally
        {
            try { await session.LogoutAsync(); } catch { /* best-effort */ }
        }

        return found;
    }

    /// <summary>
    /// Delete a folder via QNAP FileStation API.
    /// </summary>
    public static async Task DeleteViaApiAsync(
        string serverIp,
        string share,
        string username,
        string password,
        string relativePath,
        bool tlsVerify = true,
        CancellationToken ct = default)
    {
        using var session = new QnapSession(serverIp, tlsVerify);
        await session.LoginAsync(username, password);

        try
        {
            await session.DeleteFolderAsync(share, relativePath);
        }
        finally
        {
            try { await session.LogoutAsync(); } catch { /* best-effort */ }
        }
    }
}
