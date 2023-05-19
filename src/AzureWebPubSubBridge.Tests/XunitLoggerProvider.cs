using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace AzureWebPubSubBridge.Tests;

public class XunitLoggerProvider : ILoggerProvider
{
    private readonly Func<ITestOutputHelper> _testOutputHelper;

    public XunitLoggerProvider(Func<ITestOutputHelper> testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }

    public void Dispose()
    {
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new XunitLogger(_testOutputHelper, categoryName);
    }
}