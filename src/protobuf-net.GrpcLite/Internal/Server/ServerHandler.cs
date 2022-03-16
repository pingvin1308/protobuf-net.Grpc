﻿using Grpc.Core;
using Microsoft.Extensions.Logging;
using ProtoBuf.Grpc.Lite.Connections;
using System.Runtime.CompilerServices;

namespace ProtoBuf.Grpc.Lite.Internal.Server;

internal interface IServerHandler : IStream
{
    void Initialize(ushort id, IFrameConnection output, ILogger? logger);
    Status Status { get; set; }
    DateTime Deadline { get; }
    string Host { get; }
    string Peer { get; }
    CancellationToken CancellationToken { get; }
    Metadata RequestHeaders { get; }
    Metadata ResponseTrailers { get; }
    AuthContext AuthContext { get; }
    WriteOptions WriteOptions { get; set; }
    ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options);
    Task WriteResponseHeadersAsyncCore(Metadata responseHeaders);
}
internal abstract class ServerHandler<TRequest, TResponse> : HandlerBase<TResponse, TRequest>, IServerHandler, IServerStreamWriter<TResponse>
    where TResponse : class where TRequest : class
{
    protected sealed override bool IsClient => false;
    protected LiteServerCallContext CreateServerCallContext() => LiteServerCallContext.Get(this);

    protected override Action<TResponse, SerializationContext> Serializer => TypedMethod.ResponseMarshaller.ContextualSerializer;
    protected override Func<DeserializationContext, TRequest> Deserializer => TypedMethod.RequestMarshaller.ContextualDeserializer;

    private Method<TRequest, TResponse> TypedMethod => Unsafe.As<Method<TRequest, TResponse>>(Method);

    public override void Initialize(ushort id, IFrameConnection output, ILogger? logger)
    {
        base.Initialize(id, output, logger);
        Status = Status.DefaultSuccess;
        Deadline = DateTime.MaxValue;
        Host = Peer = "";
        CancellationToken = default;
        RequestHeaders = Metadata.Empty;
        WriteOptions = WriteOptions.Default;
        _responseTrailers = null;
    }

    protected abstract Task InvokeServerMethod(ServerCallContext context);

    protected async ValueTask InvokeAndCompleteAsync()
    {
        try
        {
            var method = Method;
            Logger.Debug(method, static (state, _) => $"invoking {state.FullName}...");
            try
            {
                var ctx = CreateServerCallContext();
                await InvokeServerMethod(ctx);
                ctx.Recycle();
                Logger.Debug(method, static (state, _) => $"completed {state.FullName}...");
            }
            catch (RpcException rpc)
            {
                Logger.Information(method!, static (state, ex) => $"rpc exception {state.FullName}: {ex!.Message}", rpc);
                var status = rpc.Status;
                if (status.StatusCode == StatusCode.OK)
                {
                    // one does not simply fail with success!
                    status = new Status(StatusCode.Unknown, status.Detail, status.DebugException);
                }
                Status = status;
            }
            catch (Exception ex)
            {
                Logger.Error(method!, static (state, ex) => $"faulted {state.FullName}: {ex!.Message}", ex);
                Status = new Status(StatusCode.Unknown, "The server encountered an error while performing the operation", ex);
            }

            await WriteStatusAndTrailers();
        }
        catch (Exception ex)
        {
            try
            {
                Logger.Critical(Method!, static (state, ex) => $"invocation failure {state.FullName}: {ex!.Message}", ex);
            }
            catch { }
        }
    }

    private Metadata? _responseTrailers;
    Metadata IServerHandler.ResponseTrailers => _responseTrailers ??= new Metadata();

    public Status Status { get; set; }
    public DateTime Deadline { get; private set; }

    public CancellationToken CancellationToken { get; private set; }

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    // therse will all be set in Initialize
    public Metadata RequestHeaders { get; private set; }
    public string Host { get; private set; }
    public string Peer { get; private set; }
    public WriteOptions WriteOptions { get; set; }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.

    public ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotSupportedException();
    public AuthContext AuthContext => throw new NotSupportedException();

    internal ValueTask WriteStatusAndTrailers()
    {
        // TODO
        return default;
    }

    public ValueTask WriteHeaders(Metadata responseHeaders)
    {
        return default;
    }

    Task IServerHandler.WriteResponseHeadersAsyncCore(Metadata responseHeaders)
        => WriteHeaders(responseHeaders).AsTask();

    Task IAsyncStreamWriter<TResponse>.WriteAsync(TResponse message)
        => SendAsync(message, PayloadFlags.None, CancellationToken.None).AsTask();
}
