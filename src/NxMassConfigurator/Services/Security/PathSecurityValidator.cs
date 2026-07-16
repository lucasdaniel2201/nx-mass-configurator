namespace NxMassConfigurator.Services.Security;

/// <summary>
/// Raised when a target path does not belong to the configured NAS share.
/// Port of Python InvalidTargetPath from path_security.py.
/// </summary>
public class InvalidTargetPathException : Exception
{
    public InvalidTargetPathException(string message) : base(message) { }
    public InvalidTargetPathException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Validation helpers for NAS paths used by destructive operations.
/// Direct port of path_security.py (Python → C#).
/// </summary>
public static class PathSecurityValidator
{
    /// <summary>
    /// Validates a relative target path (e.g. "uuid/hi_quality/AA-BB")
    /// Returns the normalized form. Rejects traversal and bad roots.
    /// </summary>
    public static string ValidateRelativeTarget(string server, string share, string relPath)
    {
        if (string.IsNullOrWhiteSpace(server))
            throw new InvalidTargetPathException("O servidor não pode ser vazio.");
        if (string.IsNullOrWhiteSpace(share))
            throw new InvalidTargetPathException("O compartilhamento não pode ser vazio.");
        if (server.Any(c => c is '\\' or '/' or ':' or '\0'))
            throw new InvalidTargetPathException("Servidor inválido.");

        var shareParts = Parts(share);
        if (shareParts.Count != 1)
            throw new InvalidTargetPathException("Compartilhamento inválido.");

        return string.Join("/", Parts(relPath));
    }

    /// <summary>
    /// Validates a UNC path and returns its path below the expected share.
    /// e.g. "\\\\nas01\\Public\\uuid\\hi_quality\\AA-BB" → "uuid/hi_quality/AA-BB"
    /// </summary>
    public static string UncToRelativeTarget(string unc, string expectedServer, string expectedShare)
    {
        if (string.IsNullOrWhiteSpace(unc) || !unc.StartsWith("\\\\"))
            throw new InvalidTargetPathException("O alvo precisa ser um caminho UNC.");

        var pieces = unc[2..].Replace("/", "\\").Split('\\');
        if (pieces.Length < 3 || pieces.Any(string.IsNullOrWhiteSpace))
            throw new InvalidTargetPathException("Caminho UNC incompleto.");

        var server = pieces[0];
        var share = pieces[1];

        if (!string.Equals(server, expectedServer?.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidTargetPathException("O servidor do alvo diverge do servidor esperado.");
        if (!string.Equals(share, expectedShare?.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidTargetPathException("O compartilhamento do alvo diverge do esperado.");

        var relPath = string.Join("/", pieces.Skip(2));
        return ValidateRelativeTarget(server, share, relPath);
    }

    /// <summary>
    /// Splits and validates a path into clean normalized parts, rejecting:
    /// - empty / whitespace-only parts
    /// - traversal segments (. or ..)
    /// - colons or null bytes
    /// - absolute paths (/ or C:)
    /// </summary>
    private static List<string> Parts(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidTargetPathException("O caminho do alvo não pode ser vazio.");

        var normalized = value.Replace('\\', '/');

        if (normalized.StartsWith('/') || (normalized.Length >= 2 && normalized[1] == ':'))
            throw new InvalidTargetPathException("Caminho absoluto não permitido.");

        var result = normalized.Split('/');

        foreach (var part in result)
        {
            if (string.IsNullOrWhiteSpace(part) || part is "." or "..")
                throw new InvalidTargetPathException("Caminho inválido: traversal ou componente vazio.");
            if (part.Any(c => c == '\0' || c == ':'))
                throw new InvalidTargetPathException("Caminho contém componente inválido.");
        }

        return result.ToList();
    }
}
