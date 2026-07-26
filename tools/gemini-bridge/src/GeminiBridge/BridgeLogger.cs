using System.Text.Json;

namespace GeminiBridge;

public interface IBridgeLogger
{
    void Info(string eventName, string message, object? data = null);

    void Warning(string eventName, string message, object? data = null);
}

public sealed class BridgeLogger : IBridgeLogger
{
    private readonly TextWriter _writer;
    private readonly bool _json;

    public BridgeLogger(TextWriter writer, string format)
    {
        _writer = writer;
        _json = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
    }

    public void Info(string eventName, string message, object? data = null) =>
        Write("info", eventName, message, data);

    public void Warning(string eventName, string message, object? data = null) =>
        Write("warning", eventName, message, data);

    private void Write(string level, string eventName, string message, object? data)
    {
        if (_json)
        {
            _writer.WriteLine(JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.UtcNow,
                level,
                @event = eventName,
                message,
                data
            }, ConfigStore.JsonOptions));
            return;
        }

        _writer.WriteLine($"[{level}] {message}");
    }
}

public sealed class NullBridgeLogger : IBridgeLogger
{
    public void Info(string eventName, string message, object? data = null)
    {
    }

    public void Warning(string eventName, string message, object? data = null)
    {
    }
}
