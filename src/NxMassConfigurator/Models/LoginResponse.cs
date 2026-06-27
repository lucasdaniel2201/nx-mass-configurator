namespace NxMassConfigurator.Models;

/// <summary>
/// Resposta de login (Bearer Token) da API NX Witness.
/// </summary>
public class LoginResponse
{
    public string Token { get; set; } = string.Empty;
    public long ExpiresInS { get; set; }
}
