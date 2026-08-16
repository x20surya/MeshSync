using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace CoreLib.Transport
{
    /// <summary>
    /// Splits a payload into MTU-sized chunks and puts them back together.
    ///
    /// This is the inverse of the problem TCP posed. A GATT write is message-oriented and
    /// arrives whole and in order, so there is nothing to frame - but it is hard-capped by
    /// the negotiated MTU, which is 20 usable bytes before negotiation and at most 512
    /// after. Anything longer has to be fragmented and reassembled, which is exactly what
    /// the BLE transports were missing and why their own comments said they could not work.
    ///
    /// Chunk layout, little endian:
    ///   [messageId u8][sequence u16][totalChunks u16][payload ...]
    ///
    /// The header is deliberately tiny because on an unnegotiated 23-byte MTU there are
    /// only 20 usable bytes to spend. The reassembled result is the same encrypted payload
    /// the TCP transport carries, so crypto, echo suppression and the activity log are
    /// unchanged across transports.
    /// </summary>
    public static class BleFragmenter
    {
        public const int HeaderSize = 5;

        /// <summary>Usable bytes in a GATT write before MTU negotiation (23 - 3 for ATT).</summary>
        public const int MinimumMtuPayload = 20;

        /// <summary>A u16 sequence caps a message at 65535 chunks.</summary>
        public const int MaxChunks = ushort.MaxValue;

        public static IReadOnlyList<byte[]> Fragment(ReadOnlySpan<byte> payload, int mtuPayload, byte messageId)
        {
            if (mtuPayload <= HeaderSize)
                throw new ArgumentOutOfRangeException(nameof(mtuPayload),
                    $"An MTU payload of {mtuPayload} bytes leaves no room beside the {HeaderSize} byte header.");

            int bodyPerChunk = mtuPayload - HeaderSize;

            // An empty payload still needs one chunk, otherwise the peer never learns the
            // message existed.
            int totalChunks = payload.Length == 0
                ? 1
                : (payload.Length + bodyPerChunk - 1) / bodyPerChunk;

            if (totalChunks > MaxChunks)
                throw new ArgumentException(
                    $"Payload of {payload.Length} bytes needs {totalChunks} chunks, over the {MaxChunks} limit.",
                    nameof(payload));

            var chunks = new List<byte[]>(totalChunks);

            for (int index = 0; index < totalChunks; index++)
            {
                int offset = index * bodyPerChunk;
                int length = Math.Min(bodyPerChunk, payload.Length - offset);
                if (length < 0) length = 0;

                var chunk = new byte[HeaderSize + length];
                chunk[0] = messageId;
                BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(1, 2), (ushort)index);
                BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(3, 2), (ushort)totalChunks);

                if (length > 0) payload.Slice(offset, length).CopyTo(chunk.AsSpan(HeaderSize));

                chunks.Add(chunk);
            }

            return chunks;
        }
    }

    /// <summary>
    /// Rebuilds a payload from <see cref="BleFragmenter"/> chunks.
    /// One instance per peer connection; not shared between peers.
    /// </summary>
    public sealed class BleReassembler
    {
        private readonly int _maxMessageBytes;
        private readonly TimeSpan _staleAfter;
        private readonly object _gate = new();

        private byte _messageId;
        private int _expectedChunks;
        private int _nextSequence;
        private byte[]? _buffer;
        private int _written;
        private DateTime _startedUtc = DateTime.MinValue;

        public BleReassembler(int maxMessageBytes = 4 * 1024 * 1024, TimeSpan? staleAfter = null)
        {
            _maxMessageBytes = maxMessageBytes;
            // A peer that walks out of range mid-message must not pin its partial buffer.
            _staleAfter = staleAfter ?? TimeSpan.FromSeconds(30);
        }

        /// <summary>True while a message is partially received.</summary>
        public bool InProgress
        {
            get { lock (_gate) return _buffer != null; }
        }

        /// <summary>
        /// Feeds one chunk in. Returns the complete payload on the final chunk, otherwise null.
        /// Malformed or out-of-order input discards the partial message rather than throwing,
        /// because a dropped BLE packet must not take the connection down.
        /// </summary>
        public byte[]? Accept(ReadOnlySpan<byte> chunk)
        {
            if (chunk.Length < BleFragmenter.HeaderSize)
            {
                Log(chunk.Length == 0 ? "Ignoring an empty chunk." : $"Ignoring a {chunk.Length} byte runt chunk.");
                return null;
            }

            byte messageId = chunk[0];
            int sequence = BinaryPrimitives.ReadUInt16LittleEndian(chunk.Slice(1, 2));
            int totalChunks = BinaryPrimitives.ReadUInt16LittleEndian(chunk.Slice(3, 2));
            var body = chunk.Slice(BleFragmenter.HeaderSize);

            if (totalChunks == 0)
            {
                Log("Ignoring a chunk claiming zero total chunks.");
                return null;
            }

            lock (_gate)
            {
                DiscardIfStale();

                bool startingNew = _buffer == null || messageId != _messageId || sequence == 0;

                if (startingNew)
                {
                    if (sequence != 0)
                    {
                        // Joined a message part-way through, which happens when the first
                        // chunks were sent before this side was listening.
                        Log($"Dropping message {messageId}: first chunk seen was #{sequence}.");
                        Reset();
                        return null;
                    }

                    long projected = (long)totalChunks * body.Length;
                    if (projected > _maxMessageBytes)
                    {
                        Log($"Refusing message {messageId}: about {projected} bytes exceeds the {_maxMessageBytes} byte limit.");
                        Reset();
                        return null;
                    }

                    if (_buffer != null) Log($"Abandoning incomplete message {_messageId} for new message {messageId}.");

                    _messageId = messageId;
                    _expectedChunks = totalChunks;
                    _nextSequence = 0;
                    _written = 0;
                    _startedUtc = DateTime.UtcNow;
                    // Sized from the first chunk, which is the largest; trimmed on completion.
                    _buffer = new byte[Math.Min((long)totalChunks * Math.Max(body.Length, 1), _maxMessageBytes)];
                }

                if (totalChunks != _expectedChunks)
                {
                    Log($"Dropping message {messageId}: chunk count changed from {_expectedChunks} to {totalChunks}.");
                    Reset();
                    return null;
                }

                if (sequence != _nextSequence)
                {
                    // GATT preserves order, so a gap means a lost write rather than reordering.
                    Log($"Dropping message {messageId}: expected chunk #{_nextSequence} but got #{sequence}.");
                    Reset();
                    return null;
                }

                if (_written + body.Length > _buffer!.Length)
                {
                    var grown = new byte[Math.Min((long)(_written + body.Length), _maxMessageBytes)];
                    if (grown.Length < _written + body.Length)
                    {
                        Log($"Dropping message {messageId}: exceeded the {_maxMessageBytes} byte limit.");
                        Reset();
                        return null;
                    }
                    Buffer.BlockCopy(_buffer, 0, grown, 0, _written);
                    _buffer = grown;
                }

                body.CopyTo(_buffer.AsSpan(_written));
                _written += body.Length;
                _nextSequence++;

                if (_nextSequence < _expectedChunks) return null;

                var result = new byte[_written];
                Buffer.BlockCopy(_buffer, 0, result, 0, _written);
                Reset();
                return result;
            }
        }

        public void Reset()
        {
            lock (_gate)
            {
                _buffer = null;
                _written = 0;
                _nextSequence = 0;
                _expectedChunks = 0;
                _startedUtc = DateTime.MinValue;
            }
        }

        private void DiscardIfStale()
        {
            if (_buffer == null) return;
            if (DateTime.UtcNow - _startedUtc <= _staleAfter) return;

            Log($"Discarding message {_messageId}: no chunk for {_staleAfter.TotalSeconds:F0}s.");
            Reset();
        }

        private static void Log(string message) => Diagnostics.Log.Write("Ble", message);
    }
}
