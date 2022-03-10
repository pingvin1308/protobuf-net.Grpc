﻿using Microsoft.Extensions.Logging;
using ProtoBuf.Grpc.Lite.Internal;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;

namespace ProtoBuf.Grpc.Lite
{
    public sealed class StreamServerConnection : IDisposable
    {
        public int Id { get; }

        private readonly StreamServer _server;
        private Stream _input, _output;
        private readonly Channel<StreamFrame> _outbound;
        private readonly ILogger? _logger;

        public Task Complete { get; }

        public StreamServerConnection(StreamServer server, Stream input, Stream output, CancellationToken cancellationToken)
        {
            if (input is null) throw new ArgumentNullException(nameof(input));
            if (output is null) throw new ArgumentNullException(nameof(output));
            if (!input.CanRead) throw new ArgumentException("Cannot read from input stream", nameof(input));
            if (!output.CanWrite) throw new ArgumentException("Cannot write to output stream", nameof(output));
            _input = input;
            _output = output;
            _logger = server.Logger;
            Id = server.NextId();
            _server = server;

            _outbound = StreamFrame.CreateChannel();
            _logger.LogDebug(Id, static (state, _) => $"connection {state} initialized; processing streams...");
            Complete = StreamFrame.WriteFromOutboundChannelToStream(_outbound, output, _logger, cancellationToken);

            _ = ConsumeAsync(cancellationToken);
        }

        private readonly ConcurrentDictionary<ushort, IHandler> _activeOperations = new ConcurrentDictionary<ushort, IHandler>();

        async Task ConsumeAsync(CancellationToken cancellationToken)
        {
            await Task.Yield();
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await StreamFrame.ReadAsync(_input, cancellationToken);
                _logger.LogDebug(frame, static (state, _) => $"received frame {state}");
                switch (frame.Kind)
                {
                    case FrameKind.Close:
                    case FrameKind.Ping:
                        var generalFlags = (GeneralFlags)frame.KindFlags;
                        if ((generalFlags & GeneralFlags.IsResponse) == 0)
                        {
                            // if this was a request, we reply in kind, but noting that it is a response
                            await _outbound.Writer.WriteAsync(new StreamFrame(frame.Kind, frame.Id, (byte)GeneralFlags.IsResponse), cancellationToken);
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
                        var method = Encoding.UTF8.GetString(frame.Buffer, frame.Offset, frame.Length);
                        var handler = _server.TryGetHandler(method, out var handlerFactory) ? handlerFactory() : null;
                        if (handler is null)
                        {
                            _logger.LogDebug(method, static (state, _) => $"method not found: {state}");
                            await _outbound.Writer.WriteAsync(new StreamFrame(FrameKind.MethodNotFound, frame.Id, 0), cancellationToken);
                        }
                        else if (handler.Kind != frame.Kind)
                        {
                            _logger.LogInformation((Expected: handler.Kind, Received: frame.Kind), static (state, _) => $"invalid method kind: expected {state.Expected}, received {state.Received}");
                            await _outbound.Writer.WriteAsync(new StreamFrame(FrameKind.Cancel, frame.Id, 0), cancellationToken);
                        }
                        else if (!_activeOperations.TryAdd(frame.Id, handler))
                        {
                            _logger.LogError(frame.Id, static (state, _) => $"duplicate id! {state}");
                            await _outbound.Writer.WriteAsync(new StreamFrame(FrameKind.Cancel, frame.Id, 0), cancellationToken);
                        }
                        else
                        {
                            _logger.LogDebug(method, static (state, _) => $"method accepted: {state}");
                        }
                        break;
                    case FrameKind.Payload:
                        if (_activeOperations.TryGetValue(frame.Id, out handler))
                        {
                            await handler.PushPayloadAsync(frame, _outbound.Writer, _logger, cancellationToken);
                        }
                        break;
                }
            }
        }



        public void Dispose()
        {
            _outbound.Writer.TryComplete();
            StreamChannel.Dispose(_input, _output);
        }
        public ValueTask DisposeAsync()
        {
            _outbound.Writer.TryComplete();
            return StreamChannel.DisposeAsync(_input, _output);
        }
    }
}
