using NxMassConfigurator.Models.Qnap;
using NxMassConfigurator.Services.Qnap;
using NxMassConfigurator.Services.Security;
using NxMassConfigurator.Services.Smb;

namespace NxMassConfigurator.Services;

/// <summary>
/// Orchestrates MAC folder search across QNAP NAS servers.
/// Tries API (fast HTTP) first, falling back to SMB.
/// Runs servers in parallel for speed.
/// </summary>
public static class SmartQnapSearch
{
    /// <summary>
    /// Smart search: tries QNAP FileStation API first, then falls back to SMB.
    /// Returns (foundPaths, methodUsed).
    /// </summary>
    public static async Task<(List<MacFoundPath> paths, string method)> SearchAsync(
        string serverIp,
        string share,
        string username,
        string password,
        string targetMacFolder,
        string subfolder,
        bool tlsVerify = true,
        IProgress<(int, string)>? progress = null,
        IProgress<string>? logProgress = null,
        CancellationToken ct = default)
    {
        // Try API first
        try
        {
            var paths = await QnapSearchService.SearchViaApiAsync(
                serverIp, share, username, password,
                targetMacFolder, subfolder, tlsVerify, progress, ct);
            return (paths, "api");
        }
        catch (Exception apiEx)
        {
            // Log API failure for diagnostics
            logProgress?.Report($"  API QNAP indisponível em {serverIp}: {apiEx.Message}");
        }

        // Fallback to SMB
        var smbPaths = await SmbSearchService.SearchAsync(
            serverIp, share, username, password,
            targetMacFolder, subfolder, progress, ct);
        return (smbPaths, "smb");
    }

    /// <summary>
    /// Run search on all enabled servers, in parallel.
    /// </summary>
    public static async Task<List<MacFoundPath>> SearchAllAsync(
        MacSearchOptions options,
        string password,
        int maxConcurrency = 4,
        IProgress<string>? logProgress = null,
        CancellationToken ct = default)
    {
        var allPaths = new List<MacFoundPath>();
        var selectedServers = options.Servers.Where(s => s.Selected).ToList();

        var semaphore = new SemaphoreSlim(maxConcurrency);

        var tasks = selectedServers.Select(async server =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                foreach (var subfolder in options.Subfolders)
                {
                    ct.ThrowIfCancellationRequested();
                    logProgress?.Report($"Varrendo \\\\{server.Ip}\\{server.Share}\\*\\{subfolder}...");

                    try
                    {
                        var (paths, method) = await SearchAsync(
                            server.Ip,
                            server.Share,
                            server.Username,
                            password,
                            options.MacAddress,
                            subfolder,
                            progress: null,
                            ct: ct);

                        lock (allPaths)
                        {
                            allPaths.AddRange(paths);
                        }

                        if (paths.Count > 0)
                            logProgress?.Report($"  {server.Ip}/{subfolder}: {paths.Count} encontrado(s) via {method}.");
                    }
                    catch (Exception ex)
                    {
                        logProgress?.Report($"  Erro em {server.Ip}/{subfolder}: {ex.Message}");
                    }
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        return allPaths;
    }
}

/// <summary>
/// Orchestrates folder deletion (API first, SMB fallback).
/// Validates target path before any destructive operation.
/// </summary>
public static class SmartQnapDelete
{
    /// <summary>
    /// Smart delete: tries QNAP FileStation API first, then falls back to SMB.
    /// Path is validated before any destructive operation.
    /// Returns "api" or "smb" to indicate which backend was used.
    /// </summary>
    public static async Task<string> DeleteAsync(
        string serverIp,
        string share,
        string username,
        string password,
        string relativePath,
        bool tlsVerify = true,
        IProgress<DeleteProgress>? progress = null,
        CancellationToken ct = default)
    {
        // Validate target before any destructive operation
        PathSecurityValidator.ValidateRelativeTarget(serverIp, share, relativePath);

        // Try API first
        try
        {
            await QnapSearchService.DeleteViaApiAsync(
                serverIp, share, username, password, relativePath, tlsVerify, ct);
            return "api";
        }
        catch (QnapApiPreSendException)
        {
            // Only a failure proven to happen before the destructive request
            // may fall back to SMB.
        }
        catch (QnapAmbiguousException)
        {
            // Ambiguous: API may have succeeded, do NOT retry via SMB
            throw;
        }

        // Fallback to SMB
        await SmbSearchService.DeleteTreeAsync(
            serverIp, share, username, password, relativePath, progress, ct);
        return "smb";
    }
}
