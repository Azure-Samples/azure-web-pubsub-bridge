using System.Buffers;
using System.Text.Json.Serialization;
using AzureWebPubSubBridge.Formats;

namespace AzureWebPubSubBridge.Messages;

// The json schema does not always have $.type as the first element which makes polymorphism fail for some
// messages. Using interfaces to handle messages.
public class PubSubMessage :
    IConnectedSystemPubSubMessage,
    IDisconnectedSystemPubSubMessage,
    IJoinGroupPubSubMessage,
    ILeaveGroupPubSubMessage,
    IAckPubSubMessage,
    ISequenceAckPubSubMessage,
    ISendToGroupPubSubMessage,
    IReceivePubSubMessage
{
    public string? From { get; set; }

    public bool? Success { get; set; }

    public Dictionary<string, string>? Error { get; set; }

    public string? Type { get; set; }

    public string? Event { get; set; }

    public string? ConnectionId { get; set; }

    public string? ReconnectionToken { get; set; }

    public string? UserId { get; set; }

    public string? Message { get; set; }

    public long? AckId { get; set; }

    public string? Group { get; set; }

    public string? FromUserId { get; set; }

    public string? DataType { get; set; }

    [JsonConverter(typeof(PubSubDataConverter))]
    public IMemoryOwner<byte>? Data { get; set; }

    public bool? NoEcho { get; set; }

    public long? SequenceId { get; set; }
}

public interface IPubSubMessage
{
    string? Type { get; }
}

public interface IConnectedSystemPubSubMessage : IPubSubMessage
{
    string? ConnectionId { get; }

    string? Event { get; }

    string? ReconnectionToken { get; }

    string? UserId { get; }
}

public interface IDisconnectedSystemPubSubMessage : IPubSubMessage
{
    string? ConnectionId { get; }

    string? Event { get; }

    string? Message { get; }
}

public interface IJoinGroupPubSubMessage : IPubSubMessage
{
    long? AckId { get; }

    string? Group { get; }
}

public interface ILeaveGroupPubSubMessage : IPubSubMessage
{
    long? AckId { get; }

    string? Group { get; }
}

public interface IAckPubSubMessage : IPubSubMessage
{
    long? AckId { get; }

    Dictionary<string, string>? Error { get; }

    bool? Success { get; }
}

public interface ISequenceAckPubSubMessage : IPubSubMessage
{
    long? SequenceId { get; }
}

public interface ISendToGroupPubSubMessage : IPubSubMessage
{
    long? AckId { get; }

    IMemoryOwner<byte>? Data { get; }

    string? DataType { get; }

    string? Group { get; }

    bool? NoEcho { get; }
}

public interface IReceivePubSubMessage : IPubSubMessage
{
    IMemoryOwner<byte>? Data { get; }

    string? DataType { get; }

    string? FromUserId { get; }

    string? Group { get; }

    long? SequenceId { get; }
}