using System.Collections.Concurrent;
using System.IO;

namespace NdzeVpn.Services;

public enum LogLevel { Debug, Info, Warn, Error }

public readonly record struct LogEntry(DateTime Timestamp, LogLevel Level, string Source, string Message)
{
    public override string ToString() =>
        $"{Timestamp:HH:mm:ss.fff}  {Level.ToString().ToUpperInvariant(),-5}  [{Source}] {Message}";
}

/// <summary>
/// Single log sink for the app, Xray and sing-box. Keeps a bounded in-memory ring for the Logs
/// page and mirrors everything to a daily file.
/// </summary>
public sealed class LogBus
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly object _fileLock = new();
    private int _capacity = 2000;

    public event EventHandler<LogEntry>? EntryAdded;

    public static LogBus Instance { get; } = new();

    public int Capacity
    {
        get => _capacity;
        set => _capacity = Math.Clamp(value, 200, 50_000);
    }

    public IReadOnlyCollection<LogEntry> Snapshot() => _entries.ToArray();

    public void Debug(string source, string message) => Write(LogLevel.Debug, source, message);
    public void Info(string source, string message) => Write(LogLevel.Info, source, message);
    public void Warn(string source, string message) => Write(LogLevel.Warn, source, message);
    public void Error(string source, string message) => Write(LogLevel.Error, source, message);

    public void Error(string source, string message, Exception ex) =>
        Write(LogLevel.Error, source, $"{message}: {ex.GetType().Name} {ex.Message}");

    public void Write(LogLevel level, string source, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        var entry = new LogEntry(DateTime.Now, level, source, message.TrimEnd());
        _entries.Enqueue(entry);

        while (_entries.Count > _capacity && _entries.TryDequeue(out _)) { }

        EntryAdded?.Invoke(this, entry);
        AppendToFile(entry);
    }

    public void Clear()
    {
        while (_entries.TryDequeue(out _)) { }
    }

    private void AppendToFile(LogEntry entry)
    {
        try
        {
            AppPaths.EnsureCreated();
            var file = Path.Combine(AppPaths.LogDir, $"ndzevpn-{DateTime.Now:yyyy-MM-dd}.log");
            lock (_fileLock)
            {
                File.AppendAllText(file, entry + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never be the thing that crashes the app.
        }
    }

    /// <summary>Delete log files older than the given number of days.</summary>
    public void Prune(int keepDays = 7)
    {
        try
        {
            if (!Directory.Exists(AppPaths.LogDir)) return;
            var cutoff = DateTime.Now.AddDays(-keepDays);
            foreach (var file in Directory.EnumerateFiles(AppPaths.LogDir, "ndzevpn-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
        }
        catch { }
    }
}
