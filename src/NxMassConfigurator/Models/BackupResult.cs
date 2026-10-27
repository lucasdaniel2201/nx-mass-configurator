namespace NxMassConfigurator.Models;

/// <summary>
/// Resultado do backup da configuração de uma câmera individual.
/// </summary>
public class BackupResult
{
    /// <summary>UUID do device no NX Witness.</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>Nome da câmera.</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>Nome do servidor onde a câmera está.</summary>
    public string ServerName { get; set; } = string.Empty;

    /// <summary>Caminho do arquivo JSON gravado (vazio se falhou).</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Status final: "success" ou "failed".</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Mensagem de erro (se Status == "failed").</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Tamanho do arquivo gravado em bytes.</summary>
    public long SizeBytes { get; set; }

    public bool IsSuccess => Status == "success";
    public bool IsFailed => Status == "failed";
}
