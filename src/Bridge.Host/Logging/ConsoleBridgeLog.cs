using Bridge.Core.Abstractions;

namespace Bridge.Host.Logging;

public sealed class ConsoleBridgeLog(bool debugEnabled) : IBridgeLog
{
    private readonly object _gate = new();

    public void Info(string message) => Write("INF", message, ConsoleColor.Gray);
    public void Warning(string message) => Write("WRN", message, ConsoleColor.Yellow);
    public void Error(string message, Exception? exception = null) =>
        Write("ERR", exception is null ? message : $"{message} {exception}", ConsoleColor.Red);

    public void Debug(string message)
    {
        if (debugEnabled)
        {
            Write("DBG", message, ConsoleColor.DarkGray);
        }
    }

    private void Write(string level, string message, ConsoleColor color)
    {
        lock (_gate)
        {
            if (Console.IsOutputRedirected)
            {
                Console.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}");
                return;
            }

            var previous = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}");
            Console.ForegroundColor = previous;
        }
    }
}
