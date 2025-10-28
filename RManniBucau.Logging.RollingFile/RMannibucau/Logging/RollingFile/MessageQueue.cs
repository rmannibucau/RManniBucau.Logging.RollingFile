using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace RManniBucau.Logging.RollingFile;

internal record LogMessage(DateTime Date, LogLevel Level, string Category, string Message);

internal class MessageQueue : IDisposable
{
    private readonly ConcurrentQueue<string> _messageQueue = new();
    private readonly RollingFileOptions _options;
    private readonly Thread _outputThread;
    private readonly Placeholders _dateManager;

    private TextWriter? _writer;
    private DateOnly? _lastDate;
    private string? _lastPath;
    private bool _disposed;
    private long _currentFileSize = 0;

#if DEBUG
    public IList<Action> OnFlush { get; private set; } = [];
#endif

    public MessageQueue(RollingFileOptions options)
    {
        _options = options;
        _dateManager = new(_options.TimeProvider);
        _outputThread = new Thread(ProcessLogQueue)
        {
            IsBackground = true,
            Name = "Rolling file logger message writer thread"
        };
        _outputThread.Start();
    }

    private TextWriter CurrentStream()
    {
        return _writer ?? GetStream(_dateManager.Current);
    }

    private TextWriter GetStream(Func<(string, int)> init)
    {
        var (Date, Counter) = init();
        var path = Path.Combine(
            _options.Directory,
            _options.Filename.Replace("{date}", Date).Replace("{counter}", Counter.ToString())
        );

        while (File.Exists(path) || File.Exists($"{path}.gz"))
        {
            (Date, Counter) = _dateManager.Next();
            path = Path.Combine(
                _options.Directory,
                _options.Filename.Replace("{date}", Date).Replace("{counter}", Counter.ToString())
            );
        }

        _writer = new StreamWriter(new FileStream(path, FileMode.OpenOrCreate), Encoding.UTF8);
        _lastPath = path;
        _lastDate = DateOnly.FromDateTime(_options.TimeProvider());
        return _writer;
    }

    public virtual void EnqueueMessage(LogLevel level, string category, string message)
    {
        var msg = new LogMessage(_options.TimeProvider(), level, category, message);
        // todo: enable to plug custom formatters?
        _messageQueue.Enqueue(Format(msg));
        lock (_messageQueue)
        {
            Monitor.PulseAll(_messageQueue);
        }
    }

    private string Format(LogMessage message)
    {
        return $"[{message.Date:o}][{message.Level}][{message.Category}] {message.Message}";
    }

    private void ProcessLogQueue()
    {
        var bulk = new List<string>(32);
        int currentBulkedSize = 0;
        void flush()
        {
            if (_lastDate is not null && _lastDate != DateOnly.FromDateTime(_options.TimeProvider())) // rotate
            {
                _writer?.Dispose();
                _writer = null;
                OnFinishedFile();
                _dateManager.Next();
            }

            var stream = CurrentStream();
            foreach (var it in bulk) // it can not 100% respect the date but better to flush like that
            {
                stream.WriteLine(it);
                _currentFileSize += it.Length;
                if (_currentFileSize > _options.MaxFileSize)
                {
                    stream.Dispose();
#if DEBUG
                    foreach (var listener in OnFlush)
                    {
                        listener();
                    }
#endif
                    _writer = null;
                    _dateManager.Next();
                    stream = CurrentStream();
                    _currentFileSize = 0;
                }
            }
            bulk.Clear();
            currentBulkedSize = 0;
            stream.Flush();
#if DEBUG
            foreach (var flushListener in OnFlush)
            {
                flushListener();
            }
#endif
        }

        while (!_disposed || !_messageQueue.IsEmpty)
        {
            while (_messageQueue.TryDequeue(out var message))
            {
                bulk.Add(message);
                currentBulkedSize += message.Length;
                if (currentBulkedSize > _options.BufferSize || _disposed)
                {
                    flush();
                }
            }
            if (bulk.Count > 0)
            {
                flush();
            }
            if (!_disposed)
            {
                lock (_messageQueue)
                {
                    Monitor.Wait(_messageQueue, _options.ForcedFlushTimeoutMillis);
                }
            }
        }
    }

    private void OnFinishedFile()
    {
        if (_options.Archive && _lastPath is not null)
        {
            var gzPath = $"{_lastPath}.gz";
            using (FileStream sourceFileStream = new FileStream(_lastPath, FileMode.Open, FileAccess.Read))
            { 
                using GZipStream compressionStream = new(new FileStream(gzPath, FileMode.Create, FileAccess.Write), CompressionMode.Compress);
                sourceFileStream.CopyTo(compressionStream);
            };
            File.Delete(_lastPath);
        }
        if (_options.MaxDays > 0)
        {
            var regex = new Regex(
                _options
                    .Filename.Replace("{date}", "(?<date>\\d{8})")
                    .Replace("{counter}", "(?<counter>\\d+)")
                    .Replace(".", "\\.") + "(.gz)?"
            );

            var today = new DateTime(
                DateOnly.FromDateTime(_options.TimeProvider()),
                TimeOnly.MinValue
            );
            foreach (var file in Directory.EnumerateFiles(_options.Directory))
            {
                var matcher = regex.Match(Path.GetFileName(file));
                if (matcher.Success)
                {
                    var capturedDate = matcher.Groups["date"].Value;
                    var date = new DateOnly(
                        int.Parse(capturedDate[..4]),
                        int.Parse(capturedDate[4..6]),
                        int.Parse(capturedDate[6..])
                    );
                    if ((today - date.ToDateTime(TimeOnly.MinValue)).Days > _options.MaxDays)
                    {
                        File.Delete(file);
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_messageQueue)
        {
            Monitor.PulseAll(_messageQueue);
        }
        try
        {
            _outputThread.Join(60_000); // let 1mn to flush pending messages
        }
        catch (ThreadStateException)
        {
            // no-op
        }

        // after previous thread stopped otherwise thread safety is not guaranteed
        _writer?.Dispose();
        _writer = null;

        GC.SuppressFinalize(this);
    }
}

internal class Placeholders
{
    private readonly Func<DateTime> _timeProvider;
    private int _counter = 0;
    private string _date;

    internal Placeholders(Func<DateTime> timeProvider)
    {
        _timeProvider = timeProvider;
        _date = CurrentDate();
    }

    private string CurrentDate()
    {
        return _timeProvider().ToString("yyyyMMdd");
    }

    internal (string Date, int Counter) Current()
    {
        var date = _timeProvider().ToString("yyyyMMdd");
        if (date != _date)
        {
            _date = date;
            _counter = 0;
        }
        return (date, _counter);
    }

    internal (string Date, int Counter) Next()
    {
        var date = _timeProvider().ToString("yyyyMMdd");
        if (date != _date)
        {
            _counter = 0;
        }
        else
        {
            _counter++;
        }
        return (date, _counter);
    }
}
