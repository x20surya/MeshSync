using System.Security.Cryptography;
using CoreLib.Transport;

namespace CoreLib.Tests;

public class BleFragmenterTests
{
    /// <summary>The worst case: an unnegotiated 23-byte MTU leaves 20 usable bytes.</summary>
    private const int MinMtu = BleFragmenter.MinimumMtuPayload;

    /// <summary>A typical negotiated MTU.</summary>
    private const int NegotiatedMtu = 512;

    private static byte[] Random(int length)
    {
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    private static byte[]? RoundTrip(byte[] payload, int mtu, byte messageId = 1)
    {
        var reassembler = new BleReassembler();
        byte[]? result = null;

        foreach (var chunk in BleFragmenter.Fragment(payload, mtu, messageId))
        {
            result = reassembler.Accept(chunk) ?? result;
        }

        return result;
    }

    [Fact]
    public void A_short_password_survives_the_smallest_possible_mtu()
    {
        // The case BLE exists for: a credential, on a link that never negotiated up.
        var payload = System.Text.Encoding.UTF8.GetBytes("correct horse battery staple");

        Assert.Equal(payload, RoundTrip(payload, MinMtu));
    }

    [Fact]
    public void A_payload_smaller_than_one_chunk_still_round_trips()
    {
        var payload = new byte[] { 1, 2, 3 };

        Assert.Equal(payload, RoundTrip(payload, MinMtu));
    }

    [Fact]
    public void An_empty_payload_round_trips()
    {
        // Must still produce a chunk, otherwise the peer never learns the message existed.
        Assert.Empty(RoundTrip(Array.Empty<byte>(), MinMtu)!);
    }

    [Fact]
    public void A_payload_that_exactly_fills_its_chunks_round_trips()
    {
        // Off-by-one territory: no partial final chunk.
        int body = MinMtu - BleFragmenter.HeaderSize;
        var payload = Random(body * 4);

        Assert.Equal(payload, RoundTrip(payload, MinMtu));
    }

    [Fact]
    public void A_payload_one_byte_over_a_chunk_boundary_round_trips()
    {
        int body = MinMtu - BleFragmenter.HeaderSize;
        var payload = Random(body * 3 + 1);

        var result = RoundTrip(payload, MinMtu);
        Assert.Equal(payload.Length, result!.Length);
        Assert.Equal(payload, result);
    }

    [Fact]
    public void A_large_payload_round_trips_at_a_negotiated_mtu()
    {
        var payload = Random(64 * 1024);

        Assert.Equal(payload, RoundTrip(payload, NegotiatedMtu));
    }

    [Fact]
    public void Every_chunk_fits_within_the_mtu()
    {
        var payload = Random(5000);

        foreach (var chunk in BleFragmenter.Fragment(payload, MinMtu, 7))
        {
            Assert.True(chunk.Length <= MinMtu,
                $"A {chunk.Length} byte chunk would be rejected by a {MinMtu} byte MTU.");
        }
    }

    [Fact]
    public void An_mtu_with_no_room_beside_the_header_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BleFragmenter.Fragment(new byte[10], BleFragmenter.HeaderSize, 0));
    }

    // ──────────────────────────── loss and corruption

    [Fact]
    public void A_dropped_middle_chunk_discards_the_message_rather_than_corrupting_it()
    {
        var payload = Random(200);
        var chunks = BleFragmenter.Fragment(payload, MinMtu, 3);
        var reassembler = new BleReassembler();

        byte[]? result = null;
        for (int i = 0; i < chunks.Count; i++)
        {
            if (i == 2) continue; // the radio lost this one
            result = reassembler.Accept(chunks[i]) ?? result;
        }

        Assert.Null(result);
    }

    [Fact]
    public void A_runt_chunk_is_ignored_without_throwing()
    {
        var reassembler = new BleReassembler();

        Assert.Null(reassembler.Accept(new byte[3]));
        Assert.Null(reassembler.Accept(Array.Empty<byte>()));
    }

    [Fact]
    public void Joining_part_way_through_a_message_is_dropped()
    {
        var chunks = BleFragmenter.Fragment(Random(200), MinMtu, 4);
        var reassembler = new BleReassembler();

        // Connected after the sender had already started.
        Assert.Null(reassembler.Accept(chunks[3]));
        Assert.False(reassembler.InProgress);
    }

    [Fact]
    public void A_new_message_abandons_an_incomplete_one()
    {
        var first = BleFragmenter.Fragment(Random(200), MinMtu, 1);
        var second = Random(60);
        var secondChunks = BleFragmenter.Fragment(second, MinMtu, 2);

        var reassembler = new BleReassembler();
        reassembler.Accept(first[0]);
        reassembler.Accept(first[1]);

        byte[]? result = null;
        foreach (var chunk in secondChunks) result = reassembler.Accept(chunk) ?? result;

        Assert.Equal(second, result);
    }

    [Fact]
    public void Consecutive_messages_both_arrive_intact()
    {
        var reassembler = new BleReassembler();
        var a = Random(120);
        var b = Random(300);

        byte[]? first = null;
        foreach (var chunk in BleFragmenter.Fragment(a, MinMtu, 10)) first = reassembler.Accept(chunk) ?? first;

        byte[]? second = null;
        foreach (var chunk in BleFragmenter.Fragment(b, MinMtu, 11)) second = reassembler.Accept(chunk) ?? second;

        Assert.Equal(a, first);
        Assert.Equal(b, second);
    }

    [Fact]
    public void A_message_over_the_size_limit_is_refused_before_allocating()
    {
        var reassembler = new BleReassembler(maxMessageBytes: 1024);
        var chunks = BleFragmenter.Fragment(Random(8192), NegotiatedMtu, 5);

        Assert.Null(reassembler.Accept(chunks[0]));
        Assert.False(reassembler.InProgress);
    }

    [Fact]
    public void A_stalled_message_is_discarded_once_it_goes_stale()
    {
        var reassembler = new BleReassembler(staleAfter: TimeSpan.FromMilliseconds(50));
        var chunks = BleFragmenter.Fragment(Random(200), MinMtu, 6);

        reassembler.Accept(chunks[0]);
        Assert.True(reassembler.InProgress);

        Thread.Sleep(120);

        // The peer walked out of range; the next traffic must not glue onto the stale head.
        Assert.Null(reassembler.Accept(chunks[1]));
        Assert.False(reassembler.InProgress);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(23)]
    [InlineData(64)]
    [InlineData(185)]
    [InlineData(512)]
    public void Round_trips_across_the_mtu_sizes_a_link_may_negotiate(int mtu)
    {
        var payload = Random(2048);

        Assert.Equal(payload, RoundTrip(payload, mtu));
    }
}
