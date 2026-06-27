namespace NxMassConfigurator.Models;

/// <summary>
/// Resultado da operação de alteração de uma câmera individual.
/// </summary>
public class OperationResult
{
    /// <summary>UUID do device no NX Witness.</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>Nome da câmera.</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>Status final: "success", "dry-run", "failed", "skipped".</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>FPS anterior (antes da alteração).</summary>
    public double? OldFps { get; set; }

    /// <summary>FPS novo (valor alvo).</summary>
    public double? NewFps { get; set; }

    /// <summary>Mensagem de erro (se Status == "failed").</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Código HTTP do erro (se aplicável).</summary>
    public int? HttpStatus { get; set; }

    public bool IsSuccess => Status == "success";
    public bool IsDryRun => Status == "dry-run";
    public bool IsFailed => Status == "failed";
}
