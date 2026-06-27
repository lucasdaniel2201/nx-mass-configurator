namespace NxMassConfigurator.Models;

/// <summary>
/// Informações de um servidor NX Witness no sistema.
/// </summary>
public class ServerInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
}
