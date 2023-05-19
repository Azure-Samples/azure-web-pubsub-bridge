using Microsoft.Extensions.Logging;
using Serilog.Sinks.SystemConsole.Themes;
using Xunit.Abstractions;

namespace AzureWebPubSubBridge.Tests;

public class XunitLogger : ILogger
{
    private readonly string _categoryName;
    private readonly Func<ITestOutputHelper> _testOutputHelper;

    public XunitLogger(Func<ITestOutputHelper> testOutputHelper, string categoryName)
    {
        _testOutputHelper = testOutputHelper;
        _categoryName = categoryName;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        try
        {
            _testOutputHelper().WriteLine($"[{logLevel}]: {_categoryName}: {formatter(state, exception)}");
        }
        catch
        {
            // ignored
        }
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return default;
    }
}

public class XunitConsoleTheme : ConsoleTheme
{
    public override bool CanBuffer => false;

    protected override int ResetCharCount { get; }

    public override void Reset(TextWriter output)
    {
    }

    public override int Set(TextWriter output, ConsoleThemeStyle style)
    {
        return 0;
    }
}

public class XunitConsoleTextWriter : StringWriter
{
    private readonly ITestOutputHelper _testOutputHelper;

    public XunitConsoleTextWriter(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }

    public override void Flush()
    {
        base.Flush();
        try
        {
            var sb = GetStringBuilder();
            _testOutputHelper.WriteLine(sb.ToString());
            sb.Clear();
        }
        catch
        {
            // ignored
        }
    }

    public override Task FlushAsync()
    {
        Flush();
        return Task.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            var sb = GetStringBuilder();
            _testOutputHelper.WriteLine(sb.ToString());
            sb.Clear();
        }
        catch
        {
            // ignored
        }

        base.Dispose();
    }
}