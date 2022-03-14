﻿using Grpc.Core;
using Microsoft.Extensions.Logging;
using ProtoBuf.Grpc.Lite.Connections;
using System.Collections.Concurrent;

namespace ProtoBuf.Grpc.Lite.Internal.Client;

internal sealed class LiteCallInvoker : CallInvoker
{
    private readonly ILogger? _logger;
    private readonly IFrameConnection _connection;
    private readonly ConcurrentDictionary<ushort, IClientHandler> _streams = new();
    private int _nextId = ushort.MaxValue; // so that our first id is zero
    private void AddStream(IClientHandler handler, IMethod method)
    {
        for (int i = 0; i < 1024; i++) // try *reasonably* hard to get a new stream id, without going mad
        {
            var id = Utilities.IncrementToUInt32(ref _nextId);
            handler.StreamId = id;
            if (_streams.TryAdd(id, handler))
            {
                handler.Initialize(method, _connection, _logger);
                return;
            }
        }
        ThrowUnableToReserve(_streams.Count);
        static void ThrowUnableToReserve(int count)
            => throw new InvalidOperationException($"It was not possible to reserve a new stream id; {count} streams are currently in use");
    }

    public LiteCallInvoker(IFrameConnection connection, ILogger? logger)
    {
        this._connection = connection;
        this._logger = logger;
    }

    public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        => SimpleUnary(method, host, options, request).GetAwaiter().GetResult();

    async Task<TResponse> SimpleUnary<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        where TRequest : class
        where TResponse : class
    {
        var handler = ClientUnaryHandler<TRequest, TResponse>.Get(options.CancellationToken);
        AddStream(handler, method);
        try
        {
            await handler.SendSingleAsync(host, options, request);
            return await handler.ResponseAsync;
        }
        finally
        {   // since we've never exposed this to the external world, we can safely recycle it
            handler.Recycle();
        }
    }

    static readonly Func<object, Task<Metadata>> s_responseHeadersAsync = static state => ((IClientHandler)state).ResponseHeadersAsync;
    static readonly Func<object, Status> s_getStatus = static state => ((IClientHandler)state).Status;
    static readonly Func<object, Metadata> s_getTrailers = static state => ((IClientHandler)state).Trailers();
    static readonly Action<object> s_dispose = static state => ((IClientHandler)state).Dispose();

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
    {
        var handler = ClientUnaryHandler<TRequest, TResponse>.Get(options.CancellationToken);
        AddStream(handler, method);
        _ = handler.SendSingleAsync(host, options, request);
        return new AsyncUnaryCall<TResponse>(handler.ResponseAsync, s_responseHeadersAsync, s_getStatus, s_getTrailers, s_dispose, handler);
    }

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options)
    {
        var handler = ClientUnaryHandler<TRequest, TResponse>.Get(options.CancellationToken);
        AddStream(handler, method);
        _ = handler.SendInitializeAsync(host, options).AsTask();
        return new AsyncClientStreamingCall<TRequest, TResponse>(handler, handler.ResponseAsync, s_responseHeadersAsync, s_getStatus, s_getTrailers, s_dispose, handler);
    }

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options)
    {
        var handler = ClientServerStreamingHandler<TRequest, TResponse>.Get();
        AddStream(handler, method);
        _ = handler.SendInitializeAsync(host, options).AsTask();
        return new AsyncDuplexStreamingCall<TRequest, TResponse>(handler, handler, s_responseHeadersAsync, s_getStatus, s_getTrailers, s_dispose, handler);
    }

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
    {
        var handler = ClientServerStreamingHandler<TRequest, TResponse>.Get();
        AddStream(handler, method);
        _ = handler.SendSingleAsync(host, options, request);
        return new AsyncServerStreamingCall<TResponse>(handler, s_responseHeadersAsync, s_getStatus, s_getTrailers, s_dispose, handler);
    }

    internal async Task ReadAllAsync(ILogger? logger, CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            await using var iter = _connection.GetAsyncEnumerator(cancellationToken);
            logger.LogDebug(true, static (state, _) => $"listening for messages from the server...");
            while (!cancellationToken.IsCancellationRequested && await iter.MoveNextAsync())
            {
                var frame = iter.Current;
                bool releaseFrame = false;
                var header = frame.GetHeader();
                logger.LogDebug(header, static (state, _) => $"received frame {state}");
                switch (header.Kind)
                {
                    case FrameKind.Close:
                    case FrameKind.Ping:
                        var generalFlags = (GeneralFlags)header.KindFlags;
                        if ((generalFlags & GeneralFlags.IsResponse) == 0)
                        {
                            // if this was a request, we reply in kind, but noting that it is a response
                            await _connection.WriteAsync(new FrameHeader(header.Kind, (byte)GeneralFlags.IsResponse, header.StreamId, header.SequenceId, 0), cancellationToken);
                        }
                        // shutdown if requested
                        if (header.Kind == FrameKind.Close)
                        {
                            _connection.Close();
                        }
                        releaseFrame = true;
                        break;
                    case FrameKind.NewStream:
                        logger.LogError(header, static (state, _) => $"server should not be initializing requests! {state}");
                        releaseFrame = true;
                        break;
                    case FrameKind.Payload:
                        if (_streams.TryGetValue(header.StreamId, out var handler))
                        {
                            _logger.LogDebug((handler: handler, frame: frame), static (state, _) => $"pushing {state.frame} to {state.handler.Method} ({state.handler.MethodType})");
                            await handler.ReceivePayloadAsync(frame, cancellationToken);
                        }
                        else
                        {
                            _logger.LogInformation(frame, static (state, _) => $"unexpected frame type {state}");
                            releaseFrame = true;
                        }
                        break;
                    default:
                        _logger.LogInformation(frame, static (state, _) => $"unexpected frame type {state}");
                        releaseFrame = true;
                        break;
                }
                if (releaseFrame)
                {
                    _logger.LogInformation(frame.Buffer, static (state, _) => $"releasing {state.Length} bytes");
                    frame.Release();
                }
            }
        }
        catch (OperationCanceledException oce) when (oce.CancellationToken == cancellationToken)
        { }
        catch (Exception ex)
        {
            logger.LogError(ex);
            throw;
        }
    }
}