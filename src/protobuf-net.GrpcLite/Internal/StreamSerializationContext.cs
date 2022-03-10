﻿using Grpc.Core;
using System.Buffers;
using System.Threading.Channels;

namespace ProtoBuf.Grpc.Lite.Internal;

internal class StreamSerializationContext : SerializationContext, IBufferWriter<byte>
{
    private static StreamSerializationContext? _spare;
    private readonly Queue<(byte[] Buffer, int Offset, int Length, bool ViaWriter)> _buffers = new();
    private byte[] _currentBuffer = Array.Empty<byte>();
    private int _offset, _remaining;

    public async ValueTask WriteAsync(ChannelWriter<StreamFrame> writer, ushort id, CancellationToken cancellationToken)
    {
        if (_buffers.TryDequeue(out var buffer))
        {
            do
            {
                int remaining = buffer.Length, offset = buffer.Offset;
                while (remaining > 0)
                {
                    var take = Math.Min(remaining, ushort.MaxValue);

                    remaining -= take;
                    byte payloadFlags = remaining == 0 && _buffers.Count == 0 ? (byte)1 : (byte)0; // 1 == "end of payload"
                    var frameFlags = buffer.ViaWriter ? FrameFlags.RecycleBuffer | FrameFlags.HeaderReserved : FrameFlags.None;
                    await writer.WriteAsync(new StreamFrame(FrameKind.Payload, id, payloadFlags, buffer.Buffer, buffer.Offset, (ushort)take, frameFlags), cancellationToken);
                    offset += take;
                }
            }
            while (_buffers.TryDequeue(out buffer));
        }
        else
        {
            // write an empty final payload if nothing was written
            await writer.WriteAsync(new StreamFrame(FrameKind.Payload, id, 1), cancellationToken);
        }
    }

    public static StreamSerializationContext Get()
        => Interlocked.Exchange(ref _spare, null) ?? new StreamSerializationContext();

    private StreamSerializationContext Reset()
    {
        _buffers.Clear();
        _offset = _remaining = 0;
        if (_currentBuffer.Length != 0)
            ArrayPool<byte>.Shared.Return(_currentBuffer);
        _currentBuffer = Array.Empty<byte>();
        return this;
    }

    public void Recycle() => _spare = this;

    private StreamSerializationContext() { }

    public override IBufferWriter<byte> GetBufferWriter() => this;

    public override void Complete(byte[] payload) => _buffers.Enqueue((payload, 0, payload.Length, false));

    public override void Complete() => Flush(false);

    //private int Available => _active.Length - _activeCommitted;
    private void Flush(bool getNew)
    {
        var written = _offset - StreamFrame.HeaderBytes;
        if (written > 0)
        {
            _buffers.Enqueue((_currentBuffer, StreamFrame.HeaderBytes, written, true));
        }
        if (getNew)
        {
            _currentBuffer = ArrayPool<byte>.Shared.Rent(2048);
            _offset = StreamFrame.HeaderBytes;
            _remaining = _currentBuffer.Length - StreamFrame.HeaderBytes;
        }
        else
        {
            _currentBuffer = Array.Empty<byte>();
            _remaining = _offset = 0;
        }
    }

    void IBufferWriter<byte>.Advance(int count)
    {
        if (count < 0 || count > _remaining) Throw(count, _remaining);
        _offset += count;
        _remaining -= count;

        static void Throw(int count, int _remaining) => throw new ArgumentOutOfRangeException(nameof(count),
            $"Advance must be called with count in the range [0, {_remaining}], but {count} was specified");
    }

    Memory<byte> IBufferWriter<byte>.GetMemory(int sizeHint)
    {
        if (Math.Max(sizeHint, 64) > _remaining) Flush(true);
        return new Memory<byte>(_currentBuffer, _offset, _remaining);
    }

    Span<byte> IBufferWriter<byte>.GetSpan(int sizeHint)
    {
        if (Math.Max(sizeHint, 64) > _remaining) Flush(true);
        return new Span<byte>(_currentBuffer, _offset, _remaining);
    }
}
