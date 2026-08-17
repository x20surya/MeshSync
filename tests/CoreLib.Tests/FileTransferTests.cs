using System.Security.Cryptography;
using CoreLib.Transport;

namespace CoreLib.Tests;

/// <summary>
/// File transfer, driven end to end through a channel that stands in for a link.
///
/// A transfer is exactly the kind of thing nobody verifies by hand twice, so the round trip is
/// worth having in full: offer, answer, chunks, hash. The failure cases matter more than the
/// happy one - a truncated or tampered file that arrives looking finished is the outcome the
/// hash exists to prevent.
/// </summary>
public class FileTransferTests
{
    private static string TempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "meshsync-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string WriteFile(string directory, string name, int bytes)
    {
        string path = Path.Combine(directory, name);
        var content = new byte[bytes];
        RandomNumberGenerator.Fill(content);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>
    /// Wires a sender straight into a receiver, so what leaves one arrives at the other in the
    /// order it was sent - which is what the real transport guarantees.
    /// </summary>
    private sealed class Channel
    {
        private readonly FileTransferReceiver _receiver;
        private readonly string _peer;

        public FileTransferSender Sender { get; }

        /// <summary>Drops everything after this many payloads, standing in for a peer that vanished.</summary>
        public int StopAfter { get; set; } = int.MaxValue;

        /// <summary>Corrupts the payload at this index, standing in for a damaged transfer.</summary>
        public int CorruptAt { get; set; } = -1;

        public bool Refuse { get; set; }

        public int Sent { get; private set; }

        public Channel(FileTransferReceiver receiver, string peer)
        {
            _receiver = receiver;
            _peer = peer;
            Sender = new FileTransferSender(Deliver);
        }

        private Task<bool> Deliver(byte contentType, byte[] body, CancellationToken token)
        {
            if (Sent >= StopAfter) return Task.FromResult(false);

            if (Sent == CorruptAt && contentType == SyncContent.FileChunk)
            {
                // Flip a byte of payload rather than of header, so the frame still parses and it
                // is the hash that has to catch it.
                if (body.Length > 12) body[12] ^= 0xFF;
            }

            Sent++;

            if (contentType == SyncContent.FileOffer)
            {
                Assert.True(FileTransferProtocol.TryParseOffer(body, out var offer));
                bool accepted = !Refuse && _receiver.Accept(_peer, offer!);
                Sender.NoteAnswer(offer!.TransferId, accepted);
            }
            else if (contentType == SyncContent.FileChunk)
            {
                Assert.True(FileTransferProtocol.TryParseChunk(body, out uint id, out long offset, out var data));
                _receiver.Accept(_peer, id, offset, data);
            }

            return Task.FromResult(true);
        }
    }

    // ── the round trip ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_file_arrives_whole_and_matches_its_hash()
    {
        string source = TempDirectory();
        string landing = TempDirectory();

        // Over one chunk, so the multi-chunk path is what is actually exercised.
        string path = WriteFile(source, "holiday.jpg", FileTransferProtocol.ChunkBytes + 4321);
        byte[] original = File.ReadAllBytes(path);

        using var receiver = new FileTransferReceiver(landing);

        ReceivedFile? arrived = null;
        receiver.FileReceived += file => arrived = file;

        var channel = new Channel(receiver, "peer-a");
        var result = await channel.Sender.SendAsync(path);

        Assert.Equal(FileSendResult.Sent, result);
        Assert.NotNull(arrived);
        Assert.Equal("holiday.jpg", arrived!.Name);
        Assert.Equal(original.Length, arrived.Size);
        Assert.Equal(original, File.ReadAllBytes(arrived.Path));
    }

    [Fact]
    public async Task An_empty_file_still_arrives()
    {
        string source = TempDirectory();
        string landing = TempDirectory();
        string path = WriteFile(source, "empty.txt", 0);

        using var receiver = new FileTransferReceiver(landing);
        ReceivedFile? arrived = null;
        receiver.FileReceived += file => arrived = file;

        var channel = new Channel(receiver, "peer-a");
        Assert.Equal(FileSendResult.Sent, await channel.Sender.SendAsync(path));
        Assert.NotNull(arrived);
        Assert.Equal(0, arrived!.Size);
    }

    [Fact]
    public async Task Progress_is_reported_up_to_the_full_size()
    {
        string source = TempDirectory();
        string landing = TempDirectory();
        int size = FileTransferProtocol.ChunkBytes * 2 + 17;
        string path = WriteFile(source, "video.mp4", size);

        using var receiver = new FileTransferReceiver(landing);
        var channel = new Channel(receiver, "peer-a");

        long lastSent = 0;
        long reportedTotal = 0;
        channel.Sender.Progress += (_, sent, total) => { lastSent = sent; reportedTotal = total; };

        Assert.Equal(FileSendResult.Sent, await channel.Sender.SendAsync(path));
        Assert.Equal(size, lastSent);
        Assert.Equal(size, reportedTotal);
    }

    // ── the failure cases ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The point of putting the hash in the offer. A damaged transfer has to be discarded, not
    /// delivered as a file that looks complete.
    /// </summary>
    [Fact]
    public async Task A_damaged_file_is_discarded_rather_than_delivered()
    {
        string source = TempDirectory();
        string landing = TempDirectory();
        string path = WriteFile(source, "holiday.jpg", FileTransferProtocol.ChunkBytes + 500);

        using var receiver = new FileTransferReceiver(landing);

        bool delivered = false;
        string? failure = null;
        receiver.FileReceived += _ => delivered = true;
        receiver.FileFailed += (_, reason) => failure = reason;

        // Payload 0 is the offer, so 1 is the first chunk.
        var channel = new Channel(receiver, "peer-a") { CorruptAt = 1 };
        await channel.Sender.SendAsync(path);

        Assert.False(delivered);
        Assert.NotNull(failure);

        // And nothing is left lying about in the landing folder.
        Assert.Empty(Directory.GetFiles(landing));
    }

    [Fact]
    public async Task A_transfer_that_stops_partway_leaves_nothing_behind()
    {
        string source = TempDirectory();
        string landing = TempDirectory();
        string path = WriteFile(source, "big.bin", FileTransferProtocol.ChunkBytes * 3);

        using var receiver = new FileTransferReceiver(landing);
        bool delivered = false;
        receiver.FileReceived += _ => delivered = true;

        // Offer plus one chunk, then the peer goes away.
        var channel = new Channel(receiver, "peer-a") { StopAfter = 2 };

        Assert.Equal(FileSendResult.Unreachable, await channel.Sender.SendAsync(path));
        Assert.False(delivered);

        // The part-file is still open, deliberately - it is disposal that cleans it up.
        receiver.Dispose();
        Assert.Empty(Directory.GetFiles(landing));
    }

    [Fact]
    public async Task A_refused_offer_stops_the_sender_without_sending_anything()
    {
        string source = TempDirectory();
        string landing = TempDirectory();
        string path = WriteFile(source, "unwanted.bin", FileTransferProtocol.ChunkBytes);

        using var receiver = new FileTransferReceiver(landing);
        var channel = new Channel(receiver, "peer-a") { Refuse = true };

        Assert.Equal(FileSendResult.Refused, await channel.Sender.SendAsync(path));

        // One payload: the offer. No chunks followed it.
        Assert.Equal(1, channel.Sent);
    }

    [Fact]
    public async Task An_unanswered_offer_gives_up_rather_than_waiting_for_ever()
    {
        string source = TempDirectory();
        string path = WriteFile(source, "ignored.bin", 64);

        // Accepts the payload and never answers, which is what a peer that has crashed looks
        // like. A short timeout, because waiting out the real half minute would put half a
        // minute on every run of the suite to prove something a second proves just as well.
        var sender = new FileTransferSender((_, _, _) => Task.FromResult(true),
                                            answerTimeout: TimeSpan.FromMilliseconds(300));

        var send = sender.SendAsync(path);
        var finished = await Task.WhenAny(send, Task.Delay(TimeSpan.FromSeconds(20)));

        Assert.Same(send, finished);
        Assert.Equal(FileSendResult.NoAnswer, await send);
    }

    /// <summary>
    /// Offsets are checked rather than trusted, so a chunk cannot be used to write somewhere
    /// else in the file or to make it bigger than what was offered.
    /// </summary>
    [Fact]
    public void A_chunk_at_the_wrong_offset_drops_the_transfer()
    {
        string landing = TempDirectory();
        using var receiver = new FileTransferReceiver(landing);

        var offer = new FileOffer(7, "thing.bin", 100, SHA256.HashData(new byte[100]));
        Assert.True(receiver.Accept("peer-a", offer));

        Assert.False(receiver.Accept("peer-a", 7, offset: 50, new byte[10]));
        Assert.False(receiver.Accept("peer-a", 7, offset: 0, new byte[10]));
    }

    [Fact]
    public void More_bytes_than_were_offered_drops_the_transfer()
    {
        string landing = TempDirectory();
        using var receiver = new FileTransferReceiver(landing);

        var offer = new FileOffer(7, "thing.bin", 10, SHA256.HashData(new byte[10]));
        Assert.True(receiver.Accept("peer-a", offer));

        Assert.False(receiver.Accept("peer-a", 7, offset: 0, new byte[11]));
    }

    [Fact]
    public void A_chunk_for_a_transfer_that_was_never_offered_is_ignored()
    {
        string landing = TempDirectory();
        using var receiver = new FileTransferReceiver(landing);

        Assert.False(receiver.Accept("peer-a", 999, offset: 0, new byte[10]));
    }

    /// <summary>
    /// Ids only mean anything to their sender, so two peers will eventually pick the same one.
    /// Keying on the peer as well is what stops that being a collision.
    /// </summary>
    [Fact]
    public void Two_peers_can_use_the_same_transfer_id_at_once()
    {
        string landing = TempDirectory();
        using var receiver = new FileTransferReceiver(landing);

        var received = new List<string>();
        receiver.FileReceived += file => received.Add($"{file.PeerFingerprint}:{file.Name}");

        byte[] fromA = "from alice"u8.ToArray();
        byte[] fromB = "from bob!!"u8.ToArray();

        Assert.True(receiver.Accept("alice", new FileOffer(1, "a.txt", fromA.Length, SHA256.HashData(fromA))));
        Assert.True(receiver.Accept("bob", new FileOffer(1, "b.txt", fromB.Length, SHA256.HashData(fromB))));

        Assert.True(receiver.Accept("alice", 1, 0, fromA));
        Assert.True(receiver.Accept("bob", 1, 0, fromB));

        Assert.Contains("alice:a.txt", received);
        Assert.Contains("bob:b.txt", received);
    }

    [Fact]
    public void An_offer_over_the_ceiling_is_refused()
    {
        string landing = TempDirectory();
        using var receiver = new FileTransferReceiver(landing);

        var offer = new FileOffer(1, "huge.bin", FileTransferProtocol.MaxFileBytes + 1, new byte[32]);
        Assert.False(receiver.Accept("peer-a", offer));
    }

    // ── framing ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_offer_round_trips()
    {
        byte[] hash = SHA256.HashData("content"u8);
        byte[] frame = FileTransferProtocol.BuildOffer(42, "notes.txt", 1234, hash);

        Assert.True(FileTransferProtocol.TryParseOffer(frame, out var offer));
        Assert.Equal(42u, offer!.TransferId);
        Assert.Equal("notes.txt", offer.Name);
        Assert.Equal(1234, offer.Size);
        Assert.Equal(hash, offer.Sha256);
    }

    [Fact]
    public void A_chunk_round_trips()
    {
        byte[] payload = "some bytes"u8.ToArray();
        byte[] frame = FileTransferProtocol.BuildChunk(9, 4096, payload);

        Assert.True(FileTransferProtocol.TryParseChunk(frame, out uint id, out long offset, out var data));
        Assert.Equal(9u, id);
        Assert.Equal(4096, offset);
        Assert.Equal(payload, data.ToArray());
    }

    [Fact]
    public void An_acknowledgement_round_trips()
    {
        Assert.True(FileTransferProtocol.TryParseAck(FileTransferProtocol.BuildAck(3, true), out uint yes, out bool accepted));
        Assert.Equal(3u, yes);
        Assert.True(accepted);

        Assert.True(FileTransferProtocol.TryParseAck(FileTransferProtocol.BuildAck(4, false), out uint no, out bool refused));
        Assert.Equal(4u, no);
        Assert.False(refused);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(11)]
    public void A_truncated_frame_is_refused(int keep)
    {
        byte[] offer = FileTransferProtocol.BuildOffer(1, "a.txt", 10, new byte[32]);
        Assert.False(FileTransferProtocol.TryParseOffer(offer.AsSpan(0, Math.Min(keep, offer.Length)).ToArray(), out _));

        byte[] chunk = FileTransferProtocol.BuildChunk(1, 0, "x"u8);
        Assert.False(FileTransferProtocol.TryParseChunk(chunk.AsSpan(0, Math.Min(keep, chunk.Length)).ToArray(), out _, out _, out _));
    }

    [Fact]
    public void An_offer_claiming_an_implausible_size_is_refused()
    {
        byte[] frame = FileTransferProtocol.BuildOffer(1, "a.txt", -1, new byte[32]);
        Assert.False(FileTransferProtocol.TryParseOffer(frame, out _));
    }

    // ── names ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The name is the one field that decides where bytes land, so it is parsed rather than
    /// trusted - even arriving inside an authenticated payload from a paired device.
    /// </summary>
    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData(@"..\..\Windows\System32\evil.dll", "evil.dll")]
    [InlineData("/absolute/path.txt", "path.txt")]
    [InlineData("C:\\Users\\someone\\thing.txt", "thing.txt")]
    [InlineData("..", "received-file")]
    [InlineData(".", "received-file")]
    [InlineData("", "received-file")]
    [InlineData("   ", "received-file")]
    [InlineData("holiday.jpg", "holiday.jpg")]
    [InlineData("a name with spaces.txt", "a name with spaces.txt")]
    [InlineData("naïve résumé.pdf", "naïve résumé.pdf")]
    public void A_name_can_only_ever_be_a_file_in_the_folder_it_is_meant_for(string given, string expected)
    {
        Assert.Equal(expected, FileTransferProtocol.SafeName(given));
    }

    [Fact]
    public void A_name_is_sanitised_on_arrival_as_well_as_on_the_way_out()
    {
        // Built by hand, so the sender's own sanitising is bypassed the way a hostile peer would.
        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes("../../escape.txt");
        var body = new byte[4 + 2 + nameBytes.Length + 8 + 32];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0, 4), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4, 2), (ushort)nameBytes.Length);
        nameBytes.CopyTo(body, 6);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(6 + nameBytes.Length, 8), 5);

        Assert.True(FileTransferProtocol.TryParseOffer(body, out var offer));
        Assert.Equal("escape.txt", offer!.Name);
    }
}
