﻿using Grpc.Core;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ProtoBuf.Grpc.Lite.Internal.Client;

internal sealed class StreamCallInvoker : CallInvoker
{
    private readonly Channel<StreamFrame> _outbound;
    private readonly ConcurrentDictionary<ushort, IClientHandler> _activeOperations = new();
    private int _nextId = -1; // so that our first id is zero
    private ushort NextId()
    {
        while (true)
        {
            var id = Interlocked.Increment(ref _nextId);
            if (id <= ushort.MaxValue && id >= 0) return (ushort)id; // in-range; that'll do

            // try to swap to zero; if we win: we are become zero
            if (Interlocked.CompareExchange(ref _nextId, 0, id) == id) return 0;

            // otherwise, redo from start
        }
    }

    public StreamCallInvoker(Channel<StreamFrame> outbound)
    {
        this._outbound = outbound;
    }

    public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        => SimpleUnary(method, host, options, request).GetAwaiter().GetResult();

    async Task<TResponse> SimpleUnary<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        where TRequest : class
        where TResponse : class
    {
        var receiver = ClientUnaryHandler<TResponse>.Get(NextId(), method.ResponseMarshaller, options.CancellationToken);
        _ = WriteAsync(receiver, method, host, options, request, isLastElement: true);
        try
        {
            return await receiver.ResponseAsync;
        }
        finally
        {   // since we've never exposed this to the external world, we can safely recycle it
            receiver.Recycle();
        }
    }

    static readonly Func<object, Task<Metadata>> s_responseHeadersAsync = static state => ((IClientHandler)state).ResponseHeadersAsync;
    static readonly Func<object, Status> s_getStatus = static state => ((IClientHandler)state).Status;
    static readonly Func<object, Metadata> s_getTrailers = static state => ((IClientHandler)state).Trailers();
    static readonly Action<object> s_dispose = static state => ((IClientHandler)state).Dispose();

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
    {
        var receiver = ClientUnaryHandler<TResponse>.Get(NextId(), method.ResponseMarshaller, options.CancellationToken);
        _ = WriteAsync(receiver, method, host, options, request, isLastElement: true);
        return new AsyncUnaryCall<TResponse>(receiver.ResponseAsync, s_responseHeadersAsync, s_getStatus, s_getTrailers, s_dispose, receiver);
    }

    internal async Task ConsumeAsync(Stream input, ILogger? logger, CancellationToken cancellationToken)
    {
        await Task.Yield();
        while (!cancellationToken.IsCancellationRequested)
        {
            var frame = await StreamFrame.ReadAsync(input, cancellationToken);
            logger.LogDebug(frame, static (state, _) => $"received frame {state}");
            switch (frame.Kind)
            {
                case FrameKind.Close:
                case FrameKind.Ping:
                    var generalFlags = (GeneralFlags)frame.KindFlags;
                    if ((generalFlags & GeneralFlags.IsResponse) == 0)
                    {
                        // if this was a request, we reply in kind, but noting that it is a response
                        await _outbound.Writer.WriteAsync(new StreamFrame(frame.Kind, frame.RequestId, (byte)GeneralFlags.IsResponse), cancellationToken);
                    }
                    // shutdown if requested
                    if (frame.Kind == FrameKind.Close)
                    {
                        _outbound.Writer.TryComplete();
                    }
                    break;
                case FrameKind.NewUnary:
                case FrameKind.NewClientStreaming:
                case FrameKind.NewServerStreaming:
                case FrameKind.NewDuplex:
                    logger.LogError(frame, static (state, _) => $"server should not be initializing requests! {state}");
                    break;
                case FrameKind.Payload:
                    if (_activeOperations.TryGetValue(frame.RequestId, out var handler))
                    {
                        await handler.PushPayloadAsync(frame, _outbound.Writer, logger, cancellationToken);
                    }
                    break;
            }
        }
    }

    private void AddReceiver(IClientHandler receiver)
    {
        if (receiver is null) ThrowNull();
        if (!_activeOperations.TryAdd(receiver!.Id, receiver)) ThrowDuplicate(receiver.Id);

        static void ThrowNull() => throw new ArgumentNullException(nameof(receiver));
        static void ThrowDuplicate(ushort id) => throw new ArgumentException($"Duplicate receiver key: {id}");
    }

    private async Task WriteAsync<TResponse, TRequest>(ClientUnaryHandler<TResponse> receiver, Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request,
        bool isLastElement)
        where TResponse : class
        where TRequest : class
    {
        StreamSerializationContext? serializationContext = null;
        bool complete = false;
        options.CancellationToken.ThrowIfCancellationRequested();
        try
        {
            AddReceiver(receiver);
            await _outbound.Writer.WriteAsync(StreamFrame.GetInitializeFrame(FrameKind.NewUnary, receiver.Id, method.FullName, host));
            serializationContext = Pool<StreamSerializationContext>.Get();
            method.RequestMarshaller.ContextualSerializer(request, serializationContext);
            await serializationContext.WritePayloadAsync(_outbound.Writer, receiver.Id, isLastElement, options.CancellationToken);
            complete = true;
        }
        catch (Exception ex)
        {
            if (receiver is not null)
            {
                _activeOperations.TryRemove(receiver.Id, out _);
                receiver?.Fault("Error writing message", ex);
            }
        }
        finally
        {
            if (!complete) 
            {
                await _outbound.Writer.WriteAsync(new StreamFrame(FrameKind.Cancel, receiver.Id, 0));
            }
            serializationContext?.Recycle();
        }
    }

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options)
    {
        throw new NotImplementedException();
    }

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options)
    {
        throw new NotImplementedException();
    }

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
    {
        throw new NotImplementedException();
    }
}
