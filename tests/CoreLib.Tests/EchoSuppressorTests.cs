using CoreLib;

namespace CoreLib.Tests;

public class EchoSuppressorTests
{
    private static byte[] Bytes(string value) => System.Text.Encoding.UTF8.GetBytes(value);

    [Fact]
    public void Content_that_was_just_received_is_treated_as_an_echo()
    {
        var suppressor = new EchoSuppressor();
        suppressor.NoteInbound(Bytes("hello from the laptop"));

        Assert.True(suppressor.IsEcho(Bytes("hello from the laptop")));
    }

    [Fact]
    public void Unrelated_content_is_not_an_echo()
    {
        var suppressor = new EchoSuppressor();
        suppressor.NoteInbound(Bytes("hello from the laptop"));

        Assert.False(suppressor.IsEcho(Bytes("something the user copied")));
    }

    /// <summary>
    /// The regression that made every item sync twice. Android raises OnPrimaryClipChanged
    /// more than once for a single clipboard change, and many Windows apps raise
    /// WM_CLIPBOARDUPDATE repeatedly. When IsEcho consumed its entry, the second
    /// notification looked like a genuine user copy and bounced the content back to the
    /// sender, which then echoed it again - so each copy arrived twice on both devices.
    /// </summary>
    [Fact]
    public void Repeated_clipboard_notifications_for_one_injection_are_all_suppressed()
    {
        var suppressor = new EchoSuppressor();
        suppressor.NoteInbound(Bytes("shared text"));

        Assert.True(suppressor.IsEcho(Bytes("shared text")));
        Assert.True(suppressor.IsEcho(Bytes("shared text")));
        Assert.True(suppressor.IsEcho(Bytes("shared text")));
    }

    [Fact]
    public void A_recopy_syncs_again_once_the_window_has_passed()
    {
        var suppressor = new EchoSuppressor(TimeSpan.FromMilliseconds(50));
        suppressor.NoteInbound(Bytes("shared text"));

        Assert.True(suppressor.IsEcho(Bytes("shared text")));

        Thread.Sleep(120);

        // Past the window this is a genuine user copy again, not our own injection.
        Assert.False(suppressor.IsEcho(Bytes("shared text")));
    }

    [Fact]
    public void Entries_expire_once_the_window_passes()
    {
        var suppressor = new EchoSuppressor(TimeSpan.FromMilliseconds(50));
        suppressor.NoteInbound(Bytes("stale"));

        Thread.Sleep(120);

        Assert.False(suppressor.IsEcho(Bytes("stale")));
    }

    [Fact]
    public void Two_payloads_in_quick_succession_are_both_suppressed()
    {
        // The old timing flag cleared itself 500 ms after the first payload, so the second
        // was echoed straight back to the sender.
        var suppressor = new EchoSuppressor();
        suppressor.NoteInbound(Bytes("first"));
        suppressor.NoteInbound(Bytes("second"));

        Assert.True(suppressor.IsEcho(Bytes("first")));
        Assert.True(suppressor.IsEcho(Bytes("second")));
    }

    [Fact]
    public void Tracking_stays_bounded_under_load()
    {
        var suppressor = new EchoSuppressor(TimeSpan.FromMinutes(5), capacity: 8);

        for (int i = 0; i < 500; i++) suppressor.NoteInbound(Bytes($"item-{i}"));

        // Older entries are evicted rather than accumulating forever.
        Assert.False(suppressor.IsEcho(Bytes("item-0")));
        Assert.True(suppressor.IsEcho(Bytes("item-499")));
    }

    [Fact]
    public void One_copy_reported_several_times_is_sent_only_once()
    {
        var suppressor = new EchoSuppressor();

        // Both platforms raise several clipboard notifications for a single user copy.
        Assert.True(suppressor.ShouldSend(Bytes("api_key_prod")));
        Assert.False(suppressor.ShouldSend(Bytes("api_key_prod")));
        Assert.False(suppressor.ShouldSend(Bytes("api_key_prod")));
    }

    [Fact]
    public void Received_content_is_never_sent_back_however_often_it_is_reported()
    {
        var suppressor = new EchoSuppressor();
        suppressor.NoteInbound(Bytes("from the laptop"));

        Assert.False(suppressor.ShouldSend(Bytes("from the laptop")));
        Assert.False(suppressor.ShouldSend(Bytes("from the laptop")));
    }

    /// <summary>
    /// The full ping-pong that made every item appear twice: A sends, B applies it, B's
    /// duplicate clipboard notification bounces it back, A applies it again, and so on.
    /// Modelled here as two suppressors, one per device.
    /// </summary>
    [Fact]
    public void Content_does_not_ping_pong_between_two_devices()
    {
        var laptop = new EchoSuppressor();
        var phone = new EchoSuppressor();

        // Laptop: user copies, first notification wins and is transmitted.
        Assert.True(laptop.ShouldSend(Bytes("secret")));
        Assert.False(laptop.ShouldSend(Bytes("secret")));

        // Phone: applies it, then reports the change twice. Neither may go back.
        phone.NoteInbound(Bytes("secret"));
        Assert.False(phone.ShouldSend(Bytes("secret")));
        Assert.False(phone.ShouldSend(Bytes("secret")));
    }

    [Fact]
    public void A_genuinely_different_copy_still_sends_immediately()
    {
        var suppressor = new EchoSuppressor();

        Assert.True(suppressor.ShouldSend(Bytes("first")));
        Assert.True(suppressor.ShouldSend(Bytes("second")));
        Assert.True(suppressor.ShouldSend(Bytes("third")));
    }

    [Fact]
    public void The_same_text_copied_again_later_still_sends()
    {
        var suppressor = new EchoSuppressor(
            window: TimeSpan.FromMilliseconds(60),
            duplicateSendWindow: TimeSpan.FromMilliseconds(60));

        Assert.True(suppressor.ShouldSend(Bytes("repeated")));

        Thread.Sleep(140);

        Assert.True(suppressor.ShouldSend(Bytes("repeated")));
    }

    /// <summary>
    /// Images do not round-trip byte-for-byte: Windows decodes the received JPEG to a bitmap
    /// and re-encodes it on capture, so the bytes coming back never match what was stored.
    /// Observed live as the laptop receiving a 16,470 byte screenshot and immediately
    /// transmitting a 49,073 byte re-encode of it back to the phone.
    /// </summary>
    [Fact]
    public void A_reencoded_image_is_not_sent_back_even_though_its_bytes_differ()
    {
        var suppressor = new EchoSuppressor();

        suppressor.NoteInbound(Bytes("original-jpeg-bytes"), SyncItemKind.Image);

        // The capture path hands back a different encoding of the same picture.
        Assert.False(suppressor.ShouldSend(Bytes("reencoded-jpeg-bytes-much-larger"), SyncItemKind.Image));
    }

    [Fact]
    public void A_new_image_sends_once_the_guard_has_passed()
    {
        var suppressor = new EchoSuppressor(imageGuardWindow: TimeSpan.FromMilliseconds(60));
        suppressor.NoteInbound(Bytes("received-image"), SyncItemKind.Image);

        Assert.False(suppressor.ShouldSend(Bytes("reencoded"), SyncItemKind.Image));

        Thread.Sleep(140);

        Assert.True(suppressor.ShouldSend(Bytes("a genuinely new image"), SyncItemKind.Image));
    }

    [Fact]
    public void The_image_guard_does_not_block_text()
    {
        var suppressor = new EchoSuppressor();
        suppressor.NoteInbound(Bytes("an image"), SyncItemKind.Image);

        // Copying text right after an image arrives is a genuine event and must still sync.
        Assert.True(suppressor.ShouldSend(Bytes("some copied text"), SyncItemKind.Text));
    }

    [Fact]
    public void Clear_forgets_everything()
    {
        var suppressor = new EchoSuppressor();
        suppressor.NoteInbound(Bytes("text"));
        suppressor.Clear();

        Assert.False(suppressor.IsEcho(Bytes("text")));
    }
}
