namespace NxMassConfigurator.Models.Qnap;

/// <summary>
/// Base exception for QNAP FileStation API errors.
/// </summary>
public class QnapApiException : Exception
{
    public QnapApiException(string message) : base(message) { }
    public QnapApiException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// The API could not reach the destructive request endpoint.
/// Only this exception type allows SMB fallback for delete operations.
/// Equivalent to ApiDeleteBeforeSendError in Python.
/// </summary>
public class QnapApiPreSendException : QnapApiException
{
    public QnapApiPreSendException(string message) : base(message) { }
    public QnapApiPreSendException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// The delete request may have reached QNAP; retrying is unsafe.
/// Equivalent to ApiDeleteAmbiguousError in Python.
/// </summary>
public class QnapAmbiguousException : QnapApiException
{
    public QnapAmbiguousException(string message) : base(message) { }
    public QnapAmbiguousException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Raised when the SMB tree cannot be inventoried completely during deletion.
/// Equivalent to SmbEnumerationError in Python.
/// </summary>
public class SmbEnumerationException : Exception
{
    public SmbEnumerationException(string message) : base(message) { }
    public SmbEnumerationException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Configuration for a single QNAP NAS server.
/// </summary>
public class NasServerConfig
{
    public string Ip { get; set; } = string.Empty;
    public string Share { get; set; } = "Public";
    public string Username { get; set; } = string.Empty;
    public string? PasswordProtected { get; set; }
    public bool Selected { get; set; } = true;

    /// <summary>
    /// Returns the server URL for QNAP API calls.
    /// </summary>
    public string ApiBaseUrl => $"https://{Ip}";

    public NasServerConfig Clone() => new()
    {
        Ip = Ip,
        Share = Share,
        Username = Username,
        PasswordProtected = PasswordProtected,
        Selected = Selected
    };
}

/// <summary>
/// Represents a found folder (MAC match) on a NAS share.
/// </summary>
public class MacFoundPath
{
    public string ServerIp { get; init; } = string.Empty;
    public string Share { get; init; } = string.Empty;
    public string UuidFolder { get; init; } = string.Empty;
    public string Subfolder { get; init; } = string.Empty;
    public string MacFolder { get; init; } = string.Empty;
    public string Method { get; init; } = "api"; // "api" or "smb"

    /// <summary>Full UNC path like \\10.81.5.14\Public\UUID\hi_quality\AA-BB</summary>
    public string UncPath => $"\\\\{ServerIp}\\{Share}\\{UuidFolder}\\{Subfolder}\\{MacFolder}";

    /// <summary>Relative path like UUID/hi_quality/AA-BB for SMB/API operations</summary>
    public string RelativePath => $"{UuidFolder}/{Subfolder}/{MacFolder}";
}

/// <summary>
/// Options for a MAC search operation.
/// </summary>
public class MacSearchOptions
{
    public string MacAddress { get; init; } = string.Empty;
    public IReadOnlyList<NasServerConfig> Servers { get; init; } = Array.Empty<NasServerConfig>();
    public IReadOnlyList<string> Subfolders { get; init; } = new[] { "hi_quality", "low_quality" };
    public bool HasCredentials => Servers.All(s => !string.IsNullOrWhiteSpace(s.Username));
}

/// <summary>
/// Progress report during deletion.
/// </summary>
public class DeleteProgress
{
    public int DeletedCount { get; init; }
    public int TotalItems { get; init; }
    public string CurrentFile { get; init; } = string.Empty;
    public string CurrentIp { get; init; } = string.Empty;
    public int FolderIndex { get; init; }
    public int TotalFolders { get; init; }
}
