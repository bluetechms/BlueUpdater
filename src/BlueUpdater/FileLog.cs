using Microsoft.Extensions.Logging;

namespace BlueUpdater;

// Daily-rolling log in %ProgramData%\BlueHMS\logs\blueupdater-yyyyMMdd.log, 30 days. Write failures are
// swallowed: logging must never take the updater down.
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int RetentionDays = 30;
    private readonly string _dir;
    private readonly string _prefix;
    private readonly object _gate = new();
    private DateTime _lastPurge = DateTime.MinValue;

    public FileLoggerProvider(string dir, string prefix)
    {
        _dir = dir;
        _prefix = prefix;
        try { Directory.CreateDirectory(_dir); } catch { /* file logging stays off */ }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() { }

    private void Write(string category, LogLevel level, string message)
    {
        lock (_gate)
        {
            try
            {
                var now = DateTime.Now;
                var tag = level switch
                {
                    LogLevel.Trace => "TRC",
                    LogLevel.Debug => "DBG",
                    LogLevel.Information => "INF",
                    LogLevel.Warning => "WRN",
                    LogLevel.Error => "ERR",
                    _ => "CRT"
                };
                var shortCat = category.Substring(category.LastIndexOf('.') + 1);
                File.AppendAllText(Path.Combine(_dir, $"{_prefix}-{now:yyyyMMdd}.log"),
                    $"{now:yyyy-MM-dd HH:mm:ss.fff} [{tag}] {shortCat}: {message}{Environment.NewLine}");

                if ((now - _lastPurge).TotalHours >= 12)
                {
                    _lastPurge = now;
                    foreach (var old in Directory.EnumerateFiles(_dir, _prefix + "-*.log"))
                        if (File.GetLastWriteTime(old) < now.AddDays(-RetentionDays)) File.Delete(old);
                }
            }
            catch
            {
                // disk full / locked: drop the line
            }
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _owner;
        private readonly string _category;

        public FileLogger(FileLoggerProvider owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= (_category.StartsWith("System.Net.Http", StringComparison.Ordinal) ? LogLevel.Warning : LogLevel.Information);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var msg = formatter(state, exception);
            if (exception != null) msg += " | " + exception.GetType().Name + ": " + exception.Message;
            _owner.Write(_category, logLevel, msg);
        }
    }
}
