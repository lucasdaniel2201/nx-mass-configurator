using System.Net;
using SMBLibrary;
using SMBLibrary.Client;
using NxMassConfigurator.Models.Qnap;
using SMBFileAttributes = SMBLibrary.FileAttributes;

namespace NxMassConfigurator.Services.Smb;

/// <summary>
/// SMB client wrapper using SMBLibrary for directory listing and file deletion.
/// Supports SMB2/SMB3 with encryption negotiation via dialect selection.
/// </summary>
public static class SmbSearchService
{
    /// <summary>
    /// Targeted 3-level search for a MAC folder via SMB.
    /// Level 0: share root → UUID folders
    /// Level 1: UUID/<subfolder>
    /// Level 2: subfolder contents → MAC match
    /// </summary>
    public static async Task<List<MacFoundPath>> SearchAsync(
        string serverIp,
        string share,
        string username,
        string password,
        string targetMacFolder,
        string subfolder,
        IProgress<(int, string)>? progress = null,
        CancellationToken ct = default)
    {
        var found = new List<MacFoundPath>();
        var targetUpper = targetMacFolder.ToUpperInvariant();
        var subfolderTrimmed = subfolder.Trim();
        var dirsScanned = 0;

        var effectiveUser = string.IsNullOrWhiteSpace(username) ? "Guest" : username.Trim();
        var effectivePass = password ?? string.Empty;

        var client = ConnectAndLogin(serverIp, effectiveUser, effectivePass);
        ISMBFileStore? fileStore = null;

        try
        {
            fileStore = client.TreeConnect(share, out _);
            if (fileStore == null)
                throw new Exception($"Falha ao conectar ao compartilhamento \\\\{serverIp}\\{share}");

            // Level 0: list UUID folders
            var topDirs = ListDirectories(fileStore, "");
            var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "@Recycle", "@Recently-Snapshot", "@Backup", "@tmp",
                "#recycle", "#snapshot", ".snapshot"
            };

            foreach (var top in topDirs)
            {
                ct.ThrowIfCancellationRequested();

                if (skipDirs.Contains(top) || top.StartsWith("@") || top.StartsWith("#"))
                    continue;

                dirsScanned++;
                progress?.Report((dirsScanned, top));

                // Level 1: check if subfolder exists inside UUID
                var subPath = $"{top}\\{subfolderTrimmed}";
                if (!DirectoryExists(fileStore, subPath))
                    continue;

                // Level 2: list subfolder contents looking for MAC
                try
                {
                    var innerDirs = ListDirectories(fileStore, subPath);
                    foreach (var name in innerDirs)
                    {
                        if (name.ToUpperInvariant() == targetUpper)
                        {
                            found.Add(new MacFoundPath
                            {
                                ServerIp = serverIp,
                                Share = share,
                                UuidFolder = top,
                                Subfolder = subfolderTrimmed,
                                MacFolder = name,
                                Method = "smb"
                            });
                        }
                    }
                }
                catch
                {
                    continue;
                }
            }
        }
        finally
        {
            fileStore?.Disconnect();
            client.Disconnect();
        }

        return found;
    }

    /// <summary>
    /// Delete a folder tree via SMB (file-by-file, directory-by-directory).
    /// Equivalent to smbclient.shutil.rmtree in Python.
    /// </summary>
    public static async Task DeleteTreeAsync(
        string serverIp,
        string share,
        string username,
        string password,
        string relativePath,
        IProgress<DeleteProgress>? progress = null,
        CancellationToken ct = default)
    {
        var effectiveUser = string.IsNullOrWhiteSpace(username) ? "Guest" : username.Trim();
        var effectivePass = password ?? string.Empty;

        var client = ConnectAndLogin(serverIp, effectiveUser, effectivePass);
        ISMBFileStore? fileStore = null;

        try
        {
            fileStore = client.TreeConnect(share, out _);
            if (fileStore == null)
                throw new Exception($"Falha ao conectar ao compartilhamento \\\\{serverIp}\\{share}");

            var winRelPath = relativePath.Replace('/', '\\').Trim('\\');

            // First pass: collect all items
            var allItems = WalkTree(fileStore, winRelPath, ct);
            var totalItems = allItems.Count;
            var deleted = 0;

            // Delete files and directories (post-order: leaves first)
            foreach (var item in allItems)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    if (item.IsDirectory)
                        DeleteDirectory(fileStore, item.FullPath);
                    else
                        DeleteFile(fileStore, item.FullPath);
                }
                catch (Exception ex) when (ex.Message.Contains("C0000034") || ex.Message.Contains("OBJECT_NAME_NOT_FOUND"))
                {
                    // File already deleted, ignore
                }

                deleted++;
                progress?.Report(new DeleteProgress
                {
                    DeletedCount = deleted,
                    TotalItems = totalItems,
                    CurrentFile = System.IO.Path.GetFileName(item.FullPath),
                    CurrentIp = serverIp,
                    FolderIndex = 0,
                    TotalFolders = 1
                });
            }

            // Delete root
            try
            {
                DeleteDirectory(fileStore, winRelPath);
            }
            catch (Exception ex) when (ex.Message.Contains("C0000034"))
            {
                // Already deleted
            }

            progress?.Report(new DeleteProgress
            {
                DeletedCount = totalItems + 1,
                TotalItems = totalItems + 1,
                CurrentFile = "(raiz)",
                CurrentIp = serverIp,
                FolderIndex = 0,
                TotalFolders = 1
            });
        }
        finally
        {
            fileStore?.Disconnect();
            client.Disconnect();
        }
    }

    /// <summary>
    /// Walk a directory tree, returning all items in post-order (files then dirs).
    /// Throws SmbEnumerationException on any enumeration failure (fail-closed).
    /// </summary>
    public static List<TreeItem> WalkTree(ISMBFileStore fileStore, string relativePath, CancellationToken ct)
    {
        var results = new List<TreeItem>();
        WalkRecursive(fileStore, relativePath, results, ct);
        return results;
    }

    private static void WalkRecursive(ISMBFileStore fileStore, string relPath, List<TreeItem> results, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        List<string> files;
        List<string> dirs;
        try
        {
            (files, dirs) = ListContents(fileStore, relPath);
        }
        catch (Exception ex)
        {
            throw new SmbEnumerationException(
                $"Falha ao enumerar a árvore SMB em {relPath}; exclusão cancelada.", ex);
        }

        // Recurse into subdirectories first (post-order)
        foreach (var dir in dirs)
        {
            var subPath = string.IsNullOrEmpty(relPath) ? dir : $"{relPath}\\{dir}";
            WalkRecursive(fileStore, subPath, results, ct);
        }

        // Then files
        foreach (var file in files)
        {
            var fullPath = string.IsNullOrEmpty(relPath) ? file : $"{relPath}\\{file}";
            results.Add(new TreeItem { FullPath = fullPath, IsDirectory = false });
        }

        // Then the directory itself
        foreach (var dir in dirs)
        {
            var fullPath = string.IsNullOrEmpty(relPath) ? dir : $"{relPath}\\{dir}";
            results.Add(new TreeItem { FullPath = fullPath, IsDirectory = true });
        }
    }

    /// <summary>
    /// List only directory names in a given path.
    /// </summary>
    public static List<string> ListDirectories(ISMBFileStore fileStore, string relPath)
    {
        var dirs = new List<string>();
        var (files, dirList) = ListContents(fileStore, relPath);
        return dirList;
    }

    /// <summary>
    /// Check if a directory exists.
    /// </summary>
    private static bool DirectoryExists(ISMBFileStore fileStore, string relPath)
    {
        object? handle = null;
        try
        {
            var status = fileStore.CreateFile(
                out handle,
                out _,
                string.IsNullOrEmpty(relPath) ? "" : relPath,
                AccessMask.GENERIC_READ,
                SMBFileAttributes.Directory,
                ShareAccess.Read,
                CreateDisposition.FILE_OPEN,
                CreateOptions.FILE_DIRECTORY_FILE,
                null);

            return status == NTStatus.STATUS_SUCCESS;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (handle != null) fileStore.CloseFile(handle);
        }
    }

    /// <summary>
    /// List all files and sub-directories in a path.
    /// Returns (files, directories).
    /// </summary>
    private static (List<string> files, List<string> dirs) ListContents(ISMBFileStore fileStore, string relPath)
    {
        var files = new List<string>();
        var dirs = new List<string>();
        object? handle = null;

        try
        {
            var path = string.IsNullOrEmpty(relPath) ? "" : relPath;
            var status = fileStore.CreateFile(
                out handle,
                out _,
                path,
                AccessMask.GENERIC_READ,
                SMBFileAttributes.Directory,
                ShareAccess.Read,
                CreateDisposition.FILE_OPEN,
                CreateOptions.FILE_DIRECTORY_FILE,
                null);

            if (status != NTStatus.STATUS_SUCCESS || handle == null)
                return (files, dirs);

            List<QueryDirectoryFileInformation>? entryList;
            status = fileStore.QueryDirectory(
                out entryList,
                handle,
                "*",
                FileInformationClass.FileDirectoryInformation);

            if (status == NTStatus.STATUS_SUCCESS && entryList != null)
            {
                foreach (var entry in entryList)
                {
                    if (entry is not FileDirectoryInformation fileInfo)
                        continue;

                    var name = fileInfo.FileName;
                    if (name == "." || name == "..") continue;

                    if ((fileInfo.FileAttributes & SMBFileAttributes.Directory) == SMBFileAttributes.Directory)
                        dirs.Add(name);
                    else
                        files.Add(name);
                }
            }
        }
        finally
        {
            if (handle != null) fileStore.CloseFile(handle);
        }

        return (files, dirs);
    }

    /// <summary>
    /// Delete a file via SMB.
    /// </summary>
    private static void DeleteFile(ISMBFileStore fileStore, string filePath)
    {
        object? handle = null;
        try
        {
            var status = fileStore.CreateFile(
                out handle,
                out _,
                filePath,
                AccessMask.DELETE,
                SMBFileAttributes.Normal,
                ShareAccess.None,
                CreateDisposition.FILE_OPEN,
                CreateOptions.FILE_NON_DIRECTORY_FILE,
                null);

            if (status != NTStatus.STATUS_SUCCESS)
                throw new Exception($"Falha ao abrir arquivo para exclusão: {status}");

            var dispInfo = new FileDispositionInformation { DeletePending = true };
            status = fileStore.SetFileInformation(handle, dispInfo);
            if (status != NTStatus.STATUS_SUCCESS)
                throw new Exception($"Falha ao marcar arquivo para exclusão: {status}");
        }
        finally
        {
            if (handle != null) fileStore.CloseFile(handle);
        }
    }

    /// <summary>
    /// Delete a directory via SMB.
    /// </summary>
    private static void DeleteDirectory(ISMBFileStore fileStore, string dirPath)
    {
        object? handle = null;
        try
        {
            var status = fileStore.CreateFile(
                out handle,
                out _,
                dirPath,
                AccessMask.DELETE,
                SMBFileAttributes.Directory,
                ShareAccess.None,
                CreateDisposition.FILE_OPEN,
                CreateOptions.FILE_DIRECTORY_FILE,
                null);

            if (status != NTStatus.STATUS_SUCCESS)
                throw new Exception($"Falha ao abrir diretório para exclusão: {status}");

            var dispInfo = new FileDispositionInformation { DeletePending = true };
            status = fileStore.SetFileInformation(handle, dispInfo);
            if (status != NTStatus.STATUS_SUCCESS)
                throw new Exception($"Falha ao marcar diretório para exclusão: {status}");
        }
        finally
        {
            if (handle != null) fileStore.CloseFile(handle);
        }
    }

    /// <summary>
    /// Connect and login to SMB share. Tries multiple domain formats.
    /// </summary>
    private static SMB2Client ConnectAndLogin(string serverIp, string username, string password)
    {
        var domains = new[] { string.Empty, ".", "WORKGROUP" };
        Exception? lastEx = null;

        foreach (var domain in domains)
        {
            try
            {
                var client = new SMB2Client();
                var ipAddress = IPAddress.Parse(serverIp);

                var ok = client.Connect(ipAddress, SMBTransportType.DirectTCPTransport);
                if (!ok)
                {
                    client.Disconnect();
                    continue;
                }

                NTStatus loginStatus = client.Login(domain, username, password);
                if (loginStatus == NTStatus.STATUS_SUCCESS)
                    return client;

                // STATUS_LOGON_FAILURE = wrong creds for this domain, try next
                client.Disconnect();
                if (loginStatus != NTStatus.STATUS_LOGON_FAILURE)
                    lastEx = new Exception($"Falha no login: {loginStatus}");
            }
            catch (Exception ex)
            {
                lastEx = ex;
            }

            Thread.Sleep(200);
        }

        throw new Exception(
            $"Falha ao conectar SMB em {serverIp} com usuário '{username}'. " +
            $"{(lastEx?.Message ?? "STATUS_LOGON_FAILURE — verifique usuário/senha.")}");
    }
}

/// <summary>
/// Represents an item in a directory tree walk.
/// </summary>
public sealed class TreeItem
{
    public string FullPath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
}
