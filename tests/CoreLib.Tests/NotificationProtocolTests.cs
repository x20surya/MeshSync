using CoreLib.Transport;

namespace CoreLib.Tests;

/// <summary>
/// Framing for mirrored notifications.
///
/// The fields come from whatever app posted them, so their length and content are not this
/// project's to assume. What is tested here is that nothing outside the caps gets on the wire,
/// that everything inside them survives intact, and that a malformed frame is refused rather
/// than parsed into something plausible.
/// </summary>
public class NotificationProtocolTests
{
    private static MirroredNotification Sample(string title = "Alice", string text = "See you at six") =>
        new("0|com.example.chat|42|null|10123", "com.example.chat", "Chat",
            title, text, DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));

    [Fact]
    public void A_notification_round_trips()
    {
        var original = Sample();

        Assert.True(NotificationProtocol.TryParse(NotificationProtocol.Build(original), out var parsed));

        Assert.Equal(original.Key, parsed!.Key);
        Assert.Equal(original.Package, parsed.Package);
        Assert.Equal(original.AppName, parsed.AppName);
        Assert.Equal(original.Title, parsed.Title);
        Assert.Equal(original.Text, parsed.Text);
        Assert.Equal(original.PostedUtc, parsed.PostedUtc);
    }

    [Fact]
    public void An_empty_title_or_text_still_round_trips()
    {
        // Common in practice: a title with no body, or a body with no title.
        Assert.True(NotificationProtocol.TryParse(
            NotificationProtocol.Build(Sample(title: "", text: "Just a body")), out var noTitle));
        Assert.Equal("", noTitle!.Title);
        Assert.Equal("Just a body", noTitle.Text);

        Assert.True(NotificationProtocol.TryParse(
            NotificationProtocol.Build(Sample(title: "Just a title", text: "")), out var noText));
        Assert.Equal("Just a title", noText!.Title);
        Assert.Equal("", noText.Text);
    }

    [Fact]
    public void Text_outside_ascii_survives()
    {
        var original = Sample(title: "Renée 🎉", text: "Ça va très bien - 送信しました");

        Assert.True(NotificationProtocol.TryParse(NotificationProtocol.Build(original), out var parsed));
        Assert.Equal(original.Title, parsed!.Title);
        Assert.Equal(original.Text, parsed.Text);
    }

    /// <summary>
    /// The cap is what stops a single notification occupying the Bluetooth link for as long as
    /// the sending app feels like.
    /// </summary>
    [Fact]
    public void An_enormous_body_is_cut_to_the_cap()
    {
        var original = Sample(text: new string('x', 20_000));
        byte[] frame = NotificationProtocol.Build(original);

        Assert.True(NotificationProtocol.TryParse(frame, out var parsed));
        Assert.True(parsed!.Text.Length <= NotificationProtocol.MaxTextBytes);
        Assert.StartsWith("xxx", parsed.Text);
    }

    /// <summary>
    /// Cut on a character boundary rather than a byte one, so a multi-byte body is never halved
    /// and delivered as mojibake - the same care the Bluetooth hello takes with a device name.
    /// </summary>
    [Fact]
    public void A_multi_byte_body_is_never_cut_mid_character()
    {
        var original = Sample(text: string.Concat(Enumerable.Repeat("日本語のテキスト", 400)));

        Assert.True(NotificationProtocol.TryParse(NotificationProtocol.Build(original), out var parsed));

        // Round-tripping through UTF-8 is lossless only if no character was halved: a broken
        // one would come back as a replacement character.
        Assert.DoesNotContain('�', parsed!.Text);
        Assert.True(parsed.Text.Length > 0);
    }

    [Fact]
    public void A_notification_with_no_key_is_refused()
    {
        var keyless = new MirroredNotification("", "com.example", "Example", "Title", "Text", DateTimeOffset.UtcNow);
        Assert.False(NotificationProtocol.TryParse(NotificationProtocol.Build(keyless), out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    public void A_truncated_frame_is_refused(int keep)
    {
        byte[] frame = NotificationProtocol.Build(Sample());
        Assert.False(NotificationProtocol.TryParse(frame.AsSpan(0, Math.Min(keep, frame.Length)).ToArray(), out _));
    }

    [Fact]
    public void A_dismissal_round_trips()
    {
        Assert.True(NotificationProtocol.TryParseDismiss(
            NotificationProtocol.BuildDismiss("0|com.example.chat|42|null|10123"), out string key));

        Assert.Equal("0|com.example.chat|42|null|10123", key);
    }

    [Fact]
    public void An_empty_dismissal_is_refused()
    {
        Assert.False(NotificationProtocol.TryParseDismiss(Array.Empty<byte>(), out _));
    }

    /// <summary>
    /// The key is echoed back verbatim to dismiss, so an oversized one must not be accepted -
    /// it would be sent straight back to the peer as-is.
    /// </summary>
    [Fact]
    public void An_oversized_dismissal_key_is_refused()
    {
        var oversized = new byte[NotificationProtocol.MaxKeyBytes + 1];
        Array.Fill(oversized, (byte)'a');

        Assert.False(NotificationProtocol.TryParseDismiss(oversized, out _));
    }

    /// <summary>
    /// The whole reason this feature is on the list: a notification fits comfortably in what
    /// Bluetooth can carry, so mirroring keeps working with no network at all.
    /// </summary>
    [Fact]
    public void A_typical_notification_fits_inside_the_bluetooth_ceiling()
    {
        byte[] frame = NotificationProtocol.Build(Sample());

        Assert.True(frame.Length < BleProtocol.MaxAttributeValueBytes,
            $"A typical notification is {frame.Length} bytes and should fit in a single Bluetooth write.");
    }

    [Fact]
    public void Even_a_maximal_notification_stays_well_inside_the_bluetooth_payload_limit()
    {
        var maximal = new MirroredNotification(
            new string('k', NotificationProtocol.MaxKeyBytes),
            new string('p', NotificationProtocol.MaxPackageBytes),
            new string('n', NotificationProtocol.MaxPackageBytes),
            new string('t', NotificationProtocol.MaxTitleBytes),
            new string('b', NotificationProtocol.MaxTextBytes),
            DateTimeOffset.UtcNow);

        byte[] frame = NotificationProtocol.Build(maximal);
        Assert.True(frame.Length < BleProtocol.MaxPayloadBytes);
    }

    /// <summary>A reply action on the sending side has to survive the wire, label and all.</summary>
    [Fact]
    public void A_notification_that_can_be_replied_to_says_so()
    {
        var sent = new MirroredNotification("k1", "com.whatsapp", "WhatsApp", "Aditya",
            "are you around?", DateTimeOffset.UtcNow, canReply: true, replyLabel: "Reply");

        Assert.True(NotificationProtocol.TryParse(NotificationProtocol.Build(sent), out var got));
        Assert.True(got!.CanReply);
        Assert.Equal("Reply", got.ReplyLabel);
        Assert.Equal("are you around?", got.Text);
    }

    [Fact]
    public void A_notification_that_cannot_be_replied_to_says_that_too()
    {
        var sent = new MirroredNotification("k2", "com.bank", "Bank", "OTP", "123456",
            DateTimeOffset.UtcNow);

        Assert.True(NotificationProtocol.TryParse(NotificationProtocol.Build(sent), out var got));
        Assert.False(got!.CanReply);
        Assert.Equal("", got.ReplyLabel);
    }

    /// <summary>
    /// The reply fields are appended, so a body written by a device that predates them still
    /// parses. This is that body: the five fields and nothing after them.
    /// </summary>
    [Fact]
    public void A_notification_from_before_replies_existed_still_parses()
    {
        var full = NotificationProtocol.Build(new MirroredNotification(
            "k3", "com.app", "App", "Title", "Text", DateTimeOffset.UnixEpoch.AddSeconds(1)));

        // Walk the five length-prefixed fields to find where the old format ended.
        int at = 8;
        for (int field = 0; field < 5; field++)
            at += 2 + System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(full.AsSpan(at, 2));

        byte[] old = full.AsSpan(0, at).ToArray();

        Assert.True(NotificationProtocol.TryParse(old, out var got));
        Assert.Equal("Title", got!.Title);
        Assert.False(got.CanReply);
    }

    [Fact]
    public void A_reply_round_trips()
    {
        byte[] body = NotificationProtocol.BuildReply("0|com.whatsapp|42|null|10123", "on my way");

        Assert.True(NotificationProtocol.TryParseReply(body, out string key, out string text));
        Assert.Equal("0|com.whatsapp|42|null|10123", key);
        Assert.Equal("on my way", text);
    }

    /// <summary>A blank reply would post an empty message into somebody's conversation.</summary>
    [Fact]
    public void An_empty_reply_is_refused()
    {
        Assert.False(NotificationProtocol.TryParseReply(
            NotificationProtocol.BuildReply("k", "   "), out _, out _));

        Assert.False(NotificationProtocol.TryParseReply(
            NotificationProtocol.BuildReply("", "hello"), out _, out _));
    }

    [Fact]
    public void A_reply_longer_than_the_cap_is_trimmed_rather_than_refused()
    {
        string huge = new string('a', NotificationProtocol.MaxReplyBytes * 2);

        Assert.True(NotificationProtocol.TryParseReply(
            NotificationProtocol.BuildReply("k", huge), out _, out string text));

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(text) <= NotificationProtocol.MaxReplyBytes);
    }
}
