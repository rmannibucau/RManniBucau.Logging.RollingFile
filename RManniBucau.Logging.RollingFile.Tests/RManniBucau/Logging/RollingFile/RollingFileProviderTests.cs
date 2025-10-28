using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging;

namespace RManniBucau.Logging.RollingFile;

public class RollingFileProviderTests
{
    [Fact]
    public void LogToFile()
    {
        using var ctx = new LogTestContext(o => { });
        Assert.Empty(Directory.EnumerateFiles(ctx.Dir));

        var l1 = ctx.Factory.CreateLogger("rmannibucau.LogToFile.1");
        var l2 = ctx.Factory.CreateLogger("rmannibucau.LogToFile.2");
        Assert.Empty(Directory.EnumerateFiles(ctx.Dir));

        l1.LogInformation("this is {Value}", "great");
        Assert.True(ctx.Semaphore.Wait(30_000)); // wait for the queue to see the message, then close the provider to force a flush
        l2.LogInformation("this {Value}", "too");
        Assert.True(ctx.Semaphore.Wait(30_000));

        ctx.Provider.Dispose(); // force flush to have something on the filesystem to check

        var files = Directory.EnumerateFiles(ctx.Dir).ToList();
        Assert.Equivalent(new List<string> { Path.Combine(ctx.Dir, "log-20240714-0.log") }, files);
        Assert.Equal(
            [
                "[2024-07-14T08:00:00.0000000][Information][rmannibucau.LogToFile.1] this is great",
                "[2024-07-14T08:00:00.0000000][Information][rmannibucau.LogToFile.2] this too"
            ],
            File.ReadAllLines(Path.Combine(ctx.Dir, "log-20240714-0.log"))
        );
    }

    [Fact]
    public void Rotate()
    {
        var date = new DateTime(new DateOnly(2024, 07, 14), new TimeOnly(8, 0));
        using var ctx = new LogTestContext(
            o =>
            {
                o.Archive = false;
            },
            () => date
        );

        var l1 = ctx.Factory.CreateLogger("rmannibucau.Rotate.1");
        var l2 = ctx.Factory.CreateLogger("rmannibucau.Rotate.2");
        Assert.Empty(Directory.EnumerateFiles(ctx.Dir));

        l1.LogInformation("this is {Value}", "great");
        Assert.True(ctx.Semaphore.Wait(30_000)); // wait for the queue to see the message, then close the provider to force a flush

        Thread.Sleep(50); // just give it a chance to not be bulked

        date = new DateTime(new DateOnly(2024, 07, 15), new TimeOnly(8, 0));
        l2.LogInformation("this {Value}", "too");
        Assert.True(ctx.Semaphore.Wait(30_000));

        ctx.Provider.Dispose(); // force flush to have something on the filesystem to check

        var files = Directory.EnumerateFiles(ctx.Dir).Select(Path.GetFileName).ToList();
        Assert.Equivalent(new List<string> { "log-20240714-0.log", "log-20240715-0.log" }, files);
        Assert.Equal(
            ["[2024-07-14T08:00:00.0000000][Information][rmannibucau.Rotate.1] this is great"],
            File.ReadAllLines(Path.Combine(ctx.Dir, "log-20240714-0.log"))
        );
        Assert.Equal(["[2024-07-15T08:00:00.0000000][Information][rmannibucau.Rotate.2] this too"], File.ReadAllLines(Path.Combine(ctx.Dir, "log-20240715-0.log")));
    }

    [Fact]
    public void Archive()
    {
        var date = new DateTime(new DateOnly(2024, 07, 14), new TimeOnly(8, 0));
        using var ctx = new LogTestContext(
            o =>
            {
                // Archive is true by default
            },
            () => date
        );

        var l1 = ctx.Factory.CreateLogger("rmannibucau.Archive.1");
        var l2 = ctx.Factory.CreateLogger("rmannibucau.Archive.2");
        Assert.Empty(Directory.EnumerateFiles(ctx.Dir));

        l1.LogInformation("this is {Value}", "great");
        Assert.True(ctx.Semaphore.Wait(30_000)); // wait for the queue to see the message, then close the provider to force a flush

        Thread.Sleep(50); // just give it a chance to not be bulked

        date = new DateTime(new DateOnly(2024, 07, 15), new TimeOnly(8, 0));
        l2.LogInformation("this {Value}", "too");
        Assert.True(ctx.Semaphore.Wait(30_000));

        ctx.Provider.Dispose(); // force flush to have something on the filesystem to check

        var files = Directory.EnumerateFiles(ctx.Dir).Select(Path.GetFileName).ToList();
        Assert.Equivalent(
            new List<string> { "log-20240714-0.log.gz", "log-20240715-0.log" },
            files
        );
        Assert.Equal(["[2024-07-15T08:00:00.0000000][Information][rmannibucau.Archive.2] this too"], File.ReadAllLines(Path.Combine(ctx.Dir, "log-20240715-0.log")));

        using var input = new StreamReader(
            new GZipStream(
                new FileStream(Path.Combine(ctx.Dir, "log-20240714-0.log.gz"), FileMode.Open),
                CompressionMode.Decompress
            ),
            Encoding.UTF8
        );
        var content = input.ReadToEnd();
        Assert.Equal($"[2024-07-14T08:00:00.0000000][Information][rmannibucau.Archive.1] this is great{Environment.NewLine}", content);
    }

    [Fact]
    public void Delete()
    {
        var date = new DateTime(new DateOnly(2024, 07, 14), new TimeOnly(8, 0));
        using var ctx = new LogTestContext(
            o =>
            {
                o.Archive = false;
            },
            () => date
        );

        var l1 = ctx.Factory.CreateLogger("rmannibucau.Delete.1");
        var l2 = ctx.Factory.CreateLogger("rmannibucau.Delete.2");
        Assert.Empty(Directory.EnumerateFiles(ctx.Dir));

        l1.LogInformation("this is {Value}", "great");
        Assert.True(ctx.Semaphore.Wait(30_000)); // wait for the queue to see the message, then close the provider to force a flush

        Thread.Sleep(50); // just give it a chance to not be bulked and therefore to not ignore the date

        date = new DateTime(new DateOnly(2024, 07, 22), new TimeOnly(8, 0));
        l2.LogInformation("this {Value}", "too");
        Assert.True(ctx.Semaphore.Wait(30_000));

        ctx.Provider.Dispose(); // force flush to have something on the filesystem to check

        var files = Directory.EnumerateFiles(ctx.Dir).Select(Path.GetFileName).ToList();
        Assert.Equivalent(new List<string> { "log-20240722-0.log" }, files);
        Assert.Equal(["[2024-07-22T08:00:00.0000000][Information][rmannibucau.Delete.2] this too"], File.ReadAllLines(Path.Combine(ctx.Dir, "log-20240722-0.log")));
    }

    private class LogTestContext : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(),
            $"rmannibucau-rollingfile-{Guid.NewGuid()}"
        );
        private readonly RollingFileProvider _provider;
        private readonly ILoggerFactory _factory;

        public string Dir => _dir;
        public ILoggerFactory Factory => _factory;
        public RollingFileProvider Provider => _provider;
        public SemaphoreSlim Semaphore { get; set; } = new SemaphoreSlim(0, 1);

        public LogTestContext(
            Action<RollingFileOptions> customizer,
            Func<DateTime>? timeProvider = null
        )
        {
            Directory.CreateDirectory(_dir);
            var options = new RollingFileOptions
            {
                Directory = _dir,
                TimeProvider = timeProvider ?? DefaultTime
            };
            customizer(options);
            _provider = new RollingFileProvider(options);
            _provider.Queue.OnFlush.Add(() => Semaphore.Release());
            _factory = LoggerFactory.Create(builder =>
            {
                builder.AddProvider(_provider);
                builder.SetMinimumLevel(LogLevel.Information);
            });
        }

        private DateTime DefaultTime()
        {
            return new DateTime(new DateOnly(2024, 07, 14), new TimeOnly(8, 0));
        }

        public void Dispose()
        {
            _factory.Dispose();
            Directory.Delete(_dir, true);
        }
    }
}
