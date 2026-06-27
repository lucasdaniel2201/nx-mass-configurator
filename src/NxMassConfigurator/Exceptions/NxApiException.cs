namespace NxMassConfigurator.Exceptions;

/// <summary>
/// Exceção tipada para erros da API NX Witness.
/// Adaptada do projeto NxCameraCounter.
/// </summary>
public class NxApiException : Exception
{
    public int? HttpStatusCode { get; }
    public string? Endpoint { get; }

    public NxApiException(string message, int? httpStatusCode = null, string? endpoint = null)
        : base(message)
    {
        HttpStatusCode = httpStatusCode;
        Endpoint = endpoint;
    }

    public NxApiException(string message, Exception innerException)
        : base(message, innerException) { }
}

public class NxAuthException : NxApiException
{
    public NxAuthException(string message) : base(message, 401) { }
}

public class NxConnectionException : NxApiException
{
    public NxConnectionException(string message, Exception inner)
        : base(message, inner) { }
}

public class NxTimeoutException : NxApiException
{
    public NxTimeoutException(string message) : base(message, 408) { }
}

/// <summary>
/// Rate limit (HTTP 429). Usado para backoff e retry.
/// </summary>
public class NxRateLimitException : NxApiException
{
    public NxRateLimitException(string message) : base(message, 429) { }
}
