using System.Windows.Threading;
using NxMassConfigurator.Services;

namespace NxMassConfigurator.Wpf;

internal sealed class WpfLogger : ILogger
{
    private readonly Dispatcher _dispatcher;
    private readonly Action<string> _write;

    public WpfLogger(Dispatcher dispatcher, Action<string> write)
    {
        _dispatcher = dispatcher;
        _write = write;
    }

    public void LogInfo(string message) => Write("INFO", message);
    public void LogSucesso(string message) => Write("OK", message);
    public void LogWarn(string message) => Write("WARN", message);
    public void LogErro(string message) => Write("ERRO", message);

    private void Write(string level, string message)
    {
        var line = $"[{level}] {DateTime.Now:HH:mm:ss} {message}";
        CrashLog.Write(line);
        if (_dispatcher.CheckAccess())
            _write(line);
        else
            _dispatcher.BeginInvoke(() => _write(line));
    }
}
