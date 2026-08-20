using CoreLib;

namespace CoreLib.Tests;

/// <summary>
/// The in-memory record of what has synced, and in particular the handle a received file is
/// reopened by.
///
/// <para>That handle is the only one that will ever exist: a file saved through MediaStore on
/// Android has no path the application is permitted to know, and there is no way to ask for one
/// afterwards. Dropping it at the moment of writing leaves the file on the device and unreachable
/// from the app that put it there, which looks exactly like the transfer having failed.</para>
/// </summary>
public class SyncActivityLogTests
{
    [Fact]
    public void A_received_file_remembers_where_it_landed()
    {
        var log = new SyncActivityLog();

        log.Record(SyncDirection.Received, SyncItemKind.File, 12_000, "report.pdf",
                   "content://media/external/downloads/42");

        var entry = log.Snapshot().Single();

        Assert.Equal("content://media/external/downloads/42", entry.Location);
        Assert.True(entry.CanOpen);
    }

    [Fact]
    public void Everything_else_has_nowhere_to_go()
    {
        var log = new SyncActivityLog();

        log.Record(SyncDirection.Received, SyncItemKind.Text, 20, "hello");
        log.Record(SyncDirection.Sent, SyncItemKind.Image, 4096);

        Assert.All(log.Snapshot(), entry =>
        {
            Assert.Equal(string.Empty, entry.Location);
            Assert.False(entry.CanOpen);
        });
    }

    /// <summary>
    /// A sent file is sent from somewhere the user already had it, so there is nothing for this
    /// app to reopen and no claim to make that there is.
    /// </summary>
    [Fact]
    public void A_sent_file_is_not_offered_as_openable()
    {
        var log = new SyncActivityLog();

        log.Record(SyncDirection.Sent, SyncItemKind.File, 900, "notes.txt");

        Assert.False(log.Snapshot().Single().CanOpen);
    }

    [Fact]
    public void The_location_survives_other_entries_pushing_past_it()
    {
        var log = new SyncActivityLog(capacity: 3);

        log.Record(SyncDirection.Received, SyncItemKind.File, 1, "keep.bin", "content://kept");
        log.Record(SyncDirection.Sent, SyncItemKind.Text, 2, "one");
        log.Record(SyncDirection.Sent, SyncItemKind.Text, 3, "two");

        var file = log.Snapshot().Single(e => e.Kind == SyncItemKind.File);

        Assert.Equal("content://kept", file.Location);
    }

    /// <summary>The oldest still falls off the end; remembering a location does not pin a row.</summary>
    [Fact]
    public void Capacity_is_still_honoured()
    {
        var log = new SyncActivityLog(capacity: 2);

        log.Record(SyncDirection.Received, SyncItemKind.File, 1, "first.bin", "content://first");
        log.Record(SyncDirection.Sent, SyncItemKind.Text, 2, "one");
        log.Record(SyncDirection.Sent, SyncItemKind.Text, 3, "two");

        Assert.Equal(2, log.Snapshot().Count);
        Assert.DoesNotContain(log.Snapshot(), e => e.Kind == SyncItemKind.File);
    }
}
