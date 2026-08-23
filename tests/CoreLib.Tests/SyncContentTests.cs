using System.Reflection;
using CoreLib.Transport;

namespace CoreLib.Tests;

/// <summary>
/// The one-byte tag inside every encrypted payload.
///
/// These used to be private constants in both apps, which is exactly the duplication that lets
/// two ends of a protocol drift apart in silence. They live in one place now, and this is the
/// guard that stops a new one being given a number something else already answers to - a
/// collision there would route a file chunk into the clipboard, or worse, and would show up as
/// nothing more than an odd log line.
/// </summary>
public class SyncContentTests
{
    private static Dictionary<string, byte> AllContentTypes() =>
        typeof(SyncContent)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(byte))
            .ToDictionary(f => f.Name, f => (byte)f.GetRawConstantValue()!);

    [Fact]
    public void Every_content_type_has_its_own_number()
    {
        var types = AllContentTypes();

        var duplicates = types
            .GroupBy(pair => pair.Value)
            .Where(group => group.Count() > 1)
            .Select(group => $"0x{group.Key:X2} is used by {string.Join(" and ", group.Select(p => p.Key))}")
            .ToList();

        Assert.True(duplicates.Count == 0, string.Join("; ", duplicates));
    }

    /// <summary>
    /// Text is zero because it was the first thing this app ever carried, and changing it now
    /// would be a wire break for no gain. Worth pinning so nobody tidies it.
    /// </summary>
    [Fact]
    public void The_established_numbers_do_not_move()
    {
        Assert.Equal(0x00, SyncContent.Text);
        Assert.Equal(0x01, SyncContent.Image);
        Assert.Equal(0x02, SyncContent.Address);
    }

    [Fact]
    public void Every_content_type_is_accounted_for()
    {
        // Fails when a new one is added without a thought about the two apps that dispatch on
        // it. Adding the name here is the reminder to go and handle it in both.
        var expected = new[]
        {
            nameof(SyncContent.Text),
            nameof(SyncContent.Image),
            nameof(SyncContent.Address),
            nameof(SyncContent.FileOffer),
            nameof(SyncContent.FileAck),
            nameof(SyncContent.FileChunk),
            nameof(SyncContent.Ring),
            nameof(SyncContent.Notification),
            nameof(SyncContent.NotificationDismiss),
            nameof(SyncContent.BrowseRequest),
            nameof(SyncContent.BrowseReply),
            nameof(SyncContent.FetchRequest),
            nameof(SyncContent.NotificationReply),
            nameof(SyncContent.MeshKeyOffer)
        };

        Assert.Equal(expected.OrderBy(n => n), AllContentTypes().Keys.OrderBy(n => n));
    }
}
