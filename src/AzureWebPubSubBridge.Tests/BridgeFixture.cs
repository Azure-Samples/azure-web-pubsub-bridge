using Azure.Identity;
using Azure.Messaging.WebPubSub;
using AzureWebPubSubBridge.Protocols;
using AzureWebPubSubBridge.Tunnels;
using AzureWebPubSubBridge.Tunnels.Connections;
using AzureWebPubSubBridge.Tunnels.PubSub;
using Microsoft.Extensions.Logging;
using Serilog;
using Xunit.Abstractions;

namespace AzureWebPubSubBridge.Tests;

public class DirectBridgeFixture : BridgeFixture
{
    public DirectBridgeFixture()
    {
        Protocol = new DirectProtocol(Factory.CreateLogger<DirectProtocol>(), Config);
    }
}

public class MockPubSubTunnelFactory : IPubSubTunnelFactory
{
    public MockPubSubTunnelFactory(Func<PubSubTunnel> factory)
    {
        Factory = factory;
    }

    public Func<PubSubTunnel> Factory { get; set; }

    public PubSubTunnel Create()
    {
        return Factory();
    }
}

public abstract class BridgeFixture : IDisposable
{
    private TextWriter? _consoleError;

    private TextWriter? _consoleOut;
    private ITestOutputHelper? _testOutputHelper;

    public BridgeFixture()
    {
        var pubSubEndpoint = "https://<replace-me>.webpubsub.azure.com";
        var pubSubKey = "<replace-me>";

        if (pubSubEndpoint.Contains("<replace-me>") || pubSubKey.Contains("<replace-me>"))
            throw new InvalidOperationException("Please replace <replace-me> with your Web PubSub Endpoint and Key");

        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Console(theme: new XunitConsoleTheme(), applyThemeToRedirectedOutput: true)
            .CreateLogger();

        Factory = LoggerFactory.Create(b => b.AddSerilog(dispose: true));

        Config = new BridgeConfiguration
        {
            Port = 65535,
            PubSubEndpoint = new Uri(pubSubEndpoint),
            PubSubKey = pubSubKey,
            Hub = "sender",
            StopOnError = true,
            Connect = new ConnectConfiguration
            {
                ServerId = "test-server"
            }
        };
        ServiceClient =
            new WebPubSubServiceClient(Config.PubSubEndpoint, Config.Hub, new DefaultAzureCredential());
        TunnelFactory = new MockPubSubTunnelFactory(() => new PubSubTunnel(Factory.CreateLogger<PubSubTunnel>(),
            Reliable
                ? new ReliablePubSubWebSocketConnection(Factory.CreateLogger<ReliablePubSubWebSocketConnection>(),
                    ServiceClient)
                : new PubSubWebSocketConnection(Factory.CreateLogger<PubSubWebSocketConnection>(), ServiceClient),
            new PubSubTunnelMessageReceiver(Factory.CreateLogger<PubSubTunnelMessageReceiver>()),
            new PubSubTunnelMessageSender(Factory.CreateLogger<PubSubTunnelMessageSender>(), Config),
            Config));
        Client = new PubSubTunnelClient(Factory.CreateLogger<PubSubTunnelClient>(), TunnelFactory);

        Listener = new PubSubTunnelListener(Factory.CreateLogger<PubSubTunnelListener>(), TunnelFactory);

        Cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5000));
    }

    public CancellationTokenSource Cancel { get; set; }

    public PubSubTunnelClient Client { get; set; }

    public BridgeConfiguration Config { get; set; }

    public ReliablePubSubWebSocketConnection Connection { get; set; }

    public ILoggerFactory Factory { get; set; }

    public PubSubTunnelListener Listener { get; set; }

    public ReliablePubSubWebSocketConnection ListenerConnection { get; set; }

    public LocalForwarder? LocalForwarder { get; set; }

    public IBridgeProtocol Protocol { get; set; }

    public bool Reliable { get; set; }

    public RemoteForwarder? RemoteForwarder { get; set; }

    public Task RemoteForwarderStartTask { get; set; }

    public WebPubSubServiceClient ServiceClient { get; set; }

    public Task StartTask { get; set; }

    public ITestOutputHelper? TestOutputHelper
    {
        get => _testOutputHelper;
        set
        {
            if (value == null)
            {
                if (_consoleOut != null) Console.SetOut(_consoleOut);
                if (_consoleError != null) Console.SetError(_consoleError);
                _testOutputHelper = null;
                return;
            }

            if (_consoleOut == null && _consoleError == null)
            {
                _consoleOut = Console.Out;
                _consoleError = Console.Error;
            }

            Console.SetOut(new XunitConsoleTextWriter(value));
            Console.SetError(new XunitConsoleTextWriter(value));
            _testOutputHelper = value;
        }
    }

    public IPubSubTunnelFactory TunnelFactory { get; set; }

    public void Dispose()
    {
        LocalForwarder?.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        Listener.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public Task<PubSubTunnelStream> AcceptListenerConnectionAsync()
    {
        return Task.Run(async () =>
        {
            var (result, _) = await Listener.AcceptTunnelConnectionAsync(Cancel.Token);
            return result;
        });
    }

    public async Task<bool> StartLocalForwarderAsync(bool? reliable = false)
    {
        Reliable = reliable ?? Reliable;
        if (LocalForwarder != null) await StopLocalForwarderAsync();
        LocalForwarder = new LocalForwarder(Factory.CreateLogger<LocalForwarder>(), Config, Protocol, Client);
        StartTask = LocalForwarder.StartAsync(Cancel.Token);
        return await LocalForwarder.Started.Task;
    }

    public async Task<bool> StartRemoteForwarderAsync(bool? reliable = false)
    {
        Reliable = reliable ?? Reliable;
        if (RemoteForwarder != null) await StopRemoteForwarderAsync();
        RemoteForwarder = new RemoteForwarder(Factory.CreateLogger<RemoteForwarder>(), Config, Listener);
        RemoteForwarderStartTask = RemoteForwarder.StartAsync(Cancel.Token);
        return await RemoteForwarder.Started.Task;
    }

    public async Task StartRemoteListenerAsync()
    {
        await Listener.StartAsync(Config.Connect.ServerId, Cancel.Token);
    }

    public async Task StopLocalForwarderAsync()
    {
        if (LocalForwarder == null) return;
        await LocalForwarder.StopAsync(Cancel.Token);
        LocalForwarder = null;
    }

    public async Task StopRemoteForwarderAsync()
    {
        if (RemoteForwarder == null) return;
        await RemoteForwarder.StopAsync(Cancel.Token);
        RemoteForwarder = null;
    }
}