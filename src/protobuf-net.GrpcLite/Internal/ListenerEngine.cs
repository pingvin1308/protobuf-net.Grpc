﻿using Microsoft.Extensions.Logging;
using ProtoBuf.Grpc.Lite.Connections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace ProtoBuf.Grpc.Lite.Internal;

internal interface IConnection
{
    bool IsClient { get; }
    ChannelWriter<Frame> Output { get; }
    IAsyncEnumerable<Frame> Input { get; }
    bool TryCreateStream(in Frame initialize, [MaybeNullWhen(false)] out IStream stream);

    ConcurrentDictionary<ushort, IStream> Streams { get; }
    void Remove(ushort streamId);
    CancellationToken Shutdown { get; }

    void Close(Exception? fault);
}
internal static class ListenerEngine
{

    public async static Task RunAsync(this IConnection listener, ILogger? logger, CancellationToken cancellationToken)
    {
        try
        {
            logger.Debug(listener, static (state, _) => $"connection {state} ({(state.IsClient ? "client" : "server")}) processing streams...");
            await using var iter = listener.Input.GetAsyncEnumerator(cancellationToken);
            while (!cancellationToken.IsCancellationRequested && await iter.MoveNextAsync())
            {
                var frame = iter.Current;
                var header = frame.GetHeader();
                logger.Debug(frame, static (state, _) => $"received frame {state}");
                bool release = true;
                switch (header.Kind)
                {
                    case FrameKind.ConnectionClose:
                    case FrameKind.ConnectionPing:
                        if (header.IsClientStream != listener.IsClient)
                        {
                            // the other end is initiating; acknowledge with an empty but similar frame
                            await listener.Output.WriteAsync(new FrameHeader(header.Kind, 0, header.StreamId, header.SequenceId), cancellationToken);
                        }
                        // shutdown if requested
                        if (header.Kind == FrameKind.ConnectionClose)
                        {
                            listener.Output.Complete();
                        }
                        break;
                    case FrameKind.StreamHeader when header.IsClientStream != listener.IsClient: // a header with the "other" stream marker means
                        if (listener.Streams.ContainsKey(header.StreamId))
                        {
                            logger.Error(header.StreamId, static (state, _) => $"duplicate id! {state}");
                            await listener.Output.WriteAsync(new FrameHeader(FrameKind.StreamCancel, 0, header.StreamId, 0), cancellationToken);
                        }
                        else if (listener.TryCreateStream(in frame, out var newStream) && newStream is not null)
                        {
                            if (listener.Streams.TryAdd(header.StreamId, newStream))
                            {
                                logger.Debug(frame, static (state, _) => $"method accepted: {state.GetPayloadString()}");
                            }
                            else
                            {
                                logger.Error(header.StreamId, static (state, _) => $"duplicate id! {state}");
                                await listener.Output.WriteAsync(new FrameHeader(FrameKind.StreamCancel, 0, header.StreamId, 0), cancellationToken);
                            }
                        }
                        else
                        {
                            logger.Debug(frame, static (state, _) => $"method not found: {state.GetPayloadString()}");
                            await listener.Output.WriteAsync(new FrameHeader(FrameKind.StreamMethodNotFound, 0, header.StreamId, 0), cancellationToken);
                        }
                        break;
                    default:
                        if (listener.Streams.TryGetValue(header.StreamId, out var existingStream) && existingStream is not null)
                        {
                            logger.Debug((stream: existingStream, frame: frame), static (state, _) => $"pushing {state.frame} to {state.stream.Method} ({state.stream.MethodType})");
                            if (existingStream.TryAcceptFrame(in frame))
                            {
                                release = false;
                            }
                            else
                            {
                                logger.Information(frame, static (state, _) => $"frame {state} rejected by stream");
                            }

                            if (header.Kind == FrameKind.StreamTrailer && header.IsFinal)
                            {
                                logger.Debug(header, static (state, _) => $"removing stream {state}");
                                listener.Streams.Remove(header.StreamId, out _);
                            }
                        }
                        else
                        {
                            logger.Information(frame, static (state, _) => $"received frame for unknown stream {state}");
                        }
                        break;
                }
                if (release)
                {
                    logger.Debug(frame.TotalLength, static (state, _) => $"releasing {state} bytes");
                    frame.Release();
                }
            }

            logger.Information(listener, static (state, _) => $"connection {state} ({(state.IsClient ? "client" : "server")}) exiting cleanly");
            listener.Output.Complete(null);
        }
        catch (OperationCanceledException oce) when (oce.CancellationToken == cancellationToken)
        { } // alt-success
        catch (Exception ex)
        {
            logger.Error(ex);
            listener?.Output.Complete(ex);
            throw;
        }
        finally
        {
            logger.Information(listener, static (state, _) => $"connection {state} ({(state.IsClient ? "client" : "server")}) all done");
        }
    }

}