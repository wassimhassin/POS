using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Pos.Core.Logging;

/// <summary>
/// Minimal structured logger: one JSON object per line in logs/pos-YYYYMMDD.jsonl.
/// Message-template properties are written as named fields under "props".
/// Logging failures are swallowed — logging must never take the POS down.
/// </summary>
public sealed class JsonFileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly LogLevel _minLevel;
    private readonly Lock _gate = new();
    private StreamWriter? _writer;
    private DateOnly _currentDay;
    private bool _disposed;

    public JsonFileLoggerProvider(string directory, LogLevel minLevel = LogLevel.Information)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _minLevel = minLevel;
        Directory.CreateDirectory(directory);
    }

    public ILogger CreateLogger(string categoryName) => new JsonFileLogger(categoryName, this);

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    internal bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= _minLevel;

    internal void Write<TState>(string category, LogLevel level, EventId eventId, TState state, Exception? exception, string message)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var buffer = new ArrayBufferWriter<byte>(512);
            using (var json = new Utf8JsonWriter(buffer))
            {
                json.WriteStartObject();
                json.WriteString("ts", now);
                json.WriteString("level", level.ToString());
                json.WriteString("category", category);
                if (eventId.Id != 0 || eventId.Name is not null)
                {
                    json.WriteString("event", eventId.Name ?? eventId.Id.ToString(CultureInfo.InvariantCulture));
                }

                json.WriteString("message", message);
                if (state is IReadOnlyList<KeyValuePair<string, object?>> props && props.Count > 1)
                {
                    json.WriteStartObject("props");
                    foreach (var (key, value) in props)
                    {
                        if (key == "{OriginalFormat}")
                        {
                            continue;
                        }

                        WriteValue(json, key, value);
                    }

                    json.WriteEndObject();
                }

                if (exception is not null)
                {
                    json.WriteStartObject("exception");
                    json.WriteString("type", exception.GetType().FullName);
                    json.WriteString("message", exception.Message);
                    json.WriteString("detail", exception.ToString());
                    json.WriteEndObject();
                }

                json.WriteEndObject();
            }

            var line = Encoding.UTF8.GetString(buffer.WrittenSpan);
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                EnsureWriter(DateOnly.FromDateTime(now.UtcDateTime)).WriteLine(line);
            }
        }
#pragma warning disable CA1031 // see class remarks
        catch (Exception)
#pragma warning restore CA1031
        {
            // Intentionally ignored.
        }
    }

    private StreamWriter EnsureWriter(DateOnly day)
    {
        if (_writer is not null && day == _currentDay)
        {
            return _writer;
        }

        _writer?.Dispose();
        var path = Path.Combine(_directory, $"pos-{day:yyyyMMdd}.jsonl");
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        _currentDay = day;
        return _writer;
    }

    private static void WriteValue(Utf8JsonWriter json, string key, object? value)
    {
        switch (value)
        {
            case null: json.WriteNull(key); break;
            case bool b: json.WriteBoolean(key, b); break;
            case int i: json.WriteNumber(key, i); break;
            case long l: json.WriteNumber(key, l); break;
            case double d when double.IsFinite(d): json.WriteNumber(key, d); break;
            case decimal m: json.WriteNumber(key, m); break;
            case IFormattable f: json.WriteString(key, f.ToString(null, CultureInfo.InvariantCulture)); break;
            default: json.WriteString(key, value.ToString()); break;
        }
    }

    private sealed class JsonFileLogger(string category, JsonFileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            provider.Write(category, logLevel, eventId, state, exception, formatter(state, exception));
        }
    }
}
