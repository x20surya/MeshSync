using CoreLib.Transport;

namespace CoreLib.Tests;

/// <summary>
/// The framing for browsing. Written by one device and read by another, so every field is a
/// length the reader must not take on trust.
/// </summary>
public class BrowseProtocolTests
{
    private static BrowseEntry Entry(string name, bool directory = false, long size = 0) =>
        new(name, directory, size, new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void A_request_survives_the_round_trip()
    {
        byte[] wire = BrowseProtocol.BuildRequest("A1B2C3", "photos/holiday");

        Assert.True(BrowseProtocol.TryParseRequest(wire, out string id, out string path));
        Assert.Equal("A1B2C3", id);
        Assert.Equal("photos/holiday", path);
    }

    [Fact]
    public void A_request_for_the_top_of_a_folder_carries_an_empty_path()
    {
        byte[] wire = BrowseProtocol.BuildRequest("A1B2C3", "");

        Assert.True(BrowseProtocol.TryParseRequest(wire, out _, out string path));
        Assert.Equal("", path);
    }

    [Fact]
    public void A_reply_survives_the_round_trip()
    {
        var entries = new[] { Entry("photos", directory: true), Entry("notes.txt", size: 1234) };

        byte[] wire = BrowseProtocol.BuildReply("A1B2C3", "", BrowseStatus.Ok, entries);

        Assert.True(BrowseProtocol.TryParseReply(wire, out var reply));
        Assert.Equal(BrowseStatus.Ok, reply.Status);
        Assert.Equal("A1B2C3", reply.FolderId);
        Assert.False(reply.Truncated);

        Assert.Collection(reply.Entries,
            first => { Assert.Equal("photos", first.Name); Assert.True(first.IsDirectory); },
            second => { Assert.Equal("notes.txt", second.Name); Assert.Equal(1234, second.SizeBytes); });
    }

    [Fact]
    public void A_refusal_carries_its_reason_and_no_entries()
    {
        byte[] wire = BrowseProtocol.BuildReply("A1B2C3", "..", BrowseStatus.NotAllowed, Array.Empty<BrowseEntry>());

        Assert.True(BrowseProtocol.TryParseReply(wire, out var reply));
        Assert.Equal(BrowseStatus.NotAllowed, reply.Status);
        Assert.Empty(reply.Entries);
    }

    /// <summary>
    /// A name is chosen on the other device. Anything that could act as a path component when
    /// echoed back in a fetch is dropped on arrival rather than shown and later trusted.
    /// </summary>
    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../escape")]
    [InlineData("sub/dir")]
    [InlineData("sub\\dir")]
    public void A_name_that_could_be_a_path_is_dropped_on_arrival(string hostile)
    {
        byte[] wire = BrowseProtocol.BuildReply("A1B2C3", "", BrowseStatus.Ok,
            new[] { Entry(hostile), Entry("innocent.txt") });

        Assert.True(BrowseProtocol.TryParseReply(wire, out var reply));
        Assert.Equal("innocent.txt", Assert.Single(reply.Entries).Name);
    }

    [Fact]
    public void An_over_long_listing_is_cut_and_says_so()
    {
        var many = Enumerable.Range(0, BrowseProtocol.MaxEntries + 25)
            .Select(i => Entry($"file-{i}.txt"))
            .ToArray();

        byte[] wire = BrowseProtocol.BuildReply("A1B2C3", "", BrowseStatus.Ok, many);

        Assert.True(BrowseProtocol.TryParseReply(wire, out var reply));
        Assert.Equal(BrowseProtocol.MaxEntries, reply.Entries.Count);
        Assert.True(reply.Truncated);
    }

    [Fact]
    public void A_truncated_buffer_is_refused_rather_than_half_read()
    {
        byte[] wire = BrowseProtocol.BuildReply("A1B2C3", "", BrowseStatus.Ok, new[] { Entry("notes.txt") });

        for (int cut = 1; cut < wire.Length; cut++)
        {
            Assert.False(BrowseProtocol.TryParseReply(wire[..cut], out _),
                         $"a buffer cut to {cut} bytes was accepted");
        }
    }

    [Fact]
    public void An_empty_buffer_is_refused()
    {
        Assert.False(BrowseProtocol.TryParseReply(Array.Empty<byte>(), out _));
        Assert.False(BrowseProtocol.TryParseRequest(Array.Empty<byte>(), out _, out _));
    }

    [Fact]
    public void A_long_name_is_cut_on_a_character_boundary()
    {
        string name = new('é', 400);

        byte[] wire = BrowseProtocol.BuildReply("A1B2C3", "", BrowseStatus.Ok, new[] { Entry(name) });

        Assert.True(BrowseProtocol.TryParseReply(wire, out var reply));

        // Mangled UTF-8 would come back with a replacement character rather than the original.
        Assert.DoesNotContain('�', Assert.Single(reply.Entries).Name);
    }

    [Fact]
    public void A_negative_size_is_treated_as_nothing()
    {
        var entry = new BrowseEntry("odd.bin", false, -1, DateTime.UnixEpoch);

        byte[] wire = BrowseProtocol.BuildReply("A1B2C3", "", BrowseStatus.Ok, new[] { entry });

        Assert.True(BrowseProtocol.TryParseReply(wire, out var reply));
        Assert.Equal(0, Assert.Single(reply.Entries).SizeBytes);
    }

    [Fact]
    public void Sizes_read_the_way_a_person_would_say_them()
    {
        Assert.Equal("512 B", Entry("a", size: 512).SizeLabel);
        Assert.Equal("1.5 KB", Entry("a", size: 1536).SizeLabel);
        Assert.Equal("", Entry("a", directory: true).SizeLabel);
    }
}
