﻿using Grpc.Core;
using System.Threading.Channels;

namespace ProtoBuf.Grpc.Lite.Internal.Client;

internal sealed class ClientServerStreamingHandler<TRequest, TResponse> : ClientHandler<TRequest, TResponse>, IAsyncStreamReader<TResponse>, IReceiver<TResponse> where TResponse : class where TRequest : class
{
    private static readonly UnboundedChannelOptions s_ChannelOptions = new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = true,
        AllowSynchronousContinuations = false,
    };

    TResponse _current = default!;
    private Channel<TResponse>? _channel;

    internal static ClientServerStreamingHandler<TRequest, TResponse> Get()
    {
        var obj = AllowClientRecycling ? Pool<ClientServerStreamingHandler<TRequest, TResponse>>.Get() : new ClientServerStreamingHandler<TRequest, TResponse>();
        obj._channel = Channel.CreateUnbounded<TResponse>(s_ChannelOptions);
        return obj;
    }

    private void CompleteResponseChannel() => _channel?.Writer.TryComplete();


    public override void Recycle()
    {
        CompleteResponseChannel();
        _channel = null;
        _current = default!;
        Pool<ClientServerStreamingHandler<TRequest, TResponse>>.Put(this);
    }

    protected override void Cancel(CancellationToken cancellationToken)
        => CompleteResponseChannel();

    protected override ValueTask ReceivePayloadAsync(TResponse value, CancellationToken cancellationToken)
    {
        Logger.LogDebug(StreamId, static (state, ex) => $"adding item to sequence {state}");
        return _channel!.Writer.WriteAsync(value, cancellationToken);
    }
    public override ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        CompleteResponseChannel();
        return default;
    }

    Task<bool> IAsyncStreamReader<TResponse>.MoveNext(CancellationToken cancellationToken)
        => MoveNextAndCapture(_channel!.Reader, this, cancellationToken);

    void IReceiver<TResponse>.Receive(TResponse value) => _current = value;


    TResponse IAsyncStreamReader<TResponse>.Current => _current!; // if you call it at a time other than after MoveNext(): that's on you
}