using System.Diagnostics;

namespace TravelReady;

/// <summary>Диагностические события без записи пользовательских данных и секретов.</summary>
internal static class TravelDiagnostics
{
    public static void Info(string eventCode) => Write("INFO", eventCode, null);

    public static void Warning(string eventCode, Exception? exception = null) => Write("WARN", eventCode, exception);

    public static void Error(string eventCode, Exception exception) => Write("ERROR", eventCode, exception);

    private static void Write(string level, string eventCode, Exception? exception)
    {
        var message = $"{DateTimeOffset.UtcNow:O} [{level}] {eventCode}";
        if (exception is not null)
            message += $" ({exception.GetType().Name})";

#if DEBUG
        Debug.WriteLine(message, "TravelReady");
#endif
        Trace.WriteLine(message, "TravelReady");
    }
}
