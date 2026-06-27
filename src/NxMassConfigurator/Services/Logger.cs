namespace NxMassConfigurator.Services;

/// <summary>
/// Interface de logging simples para CLI (sem dependência de Microsoft.Extensions.Logging).
/// </summary>
public interface ILogger
{
    void LogInfo(string message);
    void LogSucesso(string message);
    void LogWarn(string message);
    void LogErro(string message);
}

/// <summary>
/// Logger de console com cores ANSI para CLI.
/// </summary>
public class ConsoleLogger : ILogger
{
    private readonly bool _verbose;

    public ConsoleLogger(bool verbose = true) => _verbose = verbose;

    public void LogInfo(string message)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[INFO] {DateTime.Now:HH:mm:ss} {message}");
        Console.ForegroundColor = prev;
    }

    public void LogSucesso(string message)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[ OK ] {DateTime.Now:HH:mm:ss} {message}");
        Console.ForegroundColor = prev;
    }

    public void LogWarn(string message)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[WARN] {DateTime.Now:HH:mm:ss} {message}");
        Console.ForegroundColor = prev;
    }

    public void LogErro(string message)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"[ERRO] {DateTime.Now:HH:mm:ss} {message}");
        Console.ForegroundColor = prev;
    }
}
