using Microsoft.Extensions.DependencyInjection;

namespace AzureWebPubSubBridge.Tunnels.PubSub;

public interface IPubSubTunnelFactory
{
    PubSubTunnel Create();
}

public class PubSubTunnelFactory : IPubSubTunnelFactory
{
    private readonly IServiceProvider _serviceProvider;

    public PubSubTunnelFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public PubSubTunnel Create()
    {
        return _serviceProvider.GetService<PubSubTunnel>()
               ?? throw new InvalidOperationException($"Could not construct a {nameof(PubSubTunnel)}");
    }
}