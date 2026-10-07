namespace Bridge.Core.Abstractions;

public interface IBridgeLog
{
    void Info(string message);
    void Warning(string message);
    void Error(string message, Exception? exception = null);
    void Debug(string message);
}
