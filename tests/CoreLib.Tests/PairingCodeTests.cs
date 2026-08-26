using CoreLib.Identity;

namespace CoreLib.Tests;

/// <summary>
/// The pairing code is the one format every head both writes and reads, and it had four
/// implementations before it had one. These are the rules that differed between them.
/// </summary>
public class PairingCodeTests
{
    private static string AKey()
    {
        using var identity = DeviceIdentity.CreateEphemeral();
        return identity.PublicKey;
    }

    [Fact]
    public void Round_trips_every_field()
    {
        string key = AKey();
        string uri = PairingCode.Build(key, "192.168.1.10", "Surya's Mesh");

        Assert.True(PairingCode.TryParse(uri, out var code, out _));

        Assert.Equal(key, code!.PublicKey);
        Assert.Equal("192.168.1.10", code.Address);
        Assert.Equal("Surya's Mesh", code.MeshName);
    }

    [Fact]
    public void Mesh_name_survives_characters_that_need_escaping()
    {
        string uri = PairingCode.Build(AKey(), "10.0.0.2", "Surya & co / home");

        Assert.True(PairingCode.TryParse(uri, out var code, out _));
        Assert.Equal("Surya & co / home", code!.MeshName);
    }

    [Fact]
    public void Port_rides_along_with_the_address()
    {
        string uri = PairingCode.Build(AKey(), "192.168.1.10:45002", null);

        Assert.True(PairingCode.TryParse(uri, out var code, out _));
        Assert.Equal("192.168.1.10:45002", code!.Address);
    }

    /// <summary>
    /// Pairing with no network at all: the inviter advertises a beacon derived from the key in
    /// the code, so a joiner needs the key and nothing else. The Android client used to refuse
    /// this outright, which is why adding a device over Bluetooth could not work from the phone.
    /// </summary>
    [Fact]
    public void Address_is_optional()
    {
        string key = AKey();
        string uri = PairingCode.Build(key, null, "Home");

        Assert.DoesNotContain("ip=", uri);
        Assert.True(PairingCode.TryParse(uri, out var code, out _));

        Assert.Equal(key, code!.PublicKey);
        Assert.Null(code.Address);
    }

    [Fact]
    public void Empty_mesh_name_is_left_out_rather_than_sent_blank()
    {
        string uri = PairingCode.Build(AKey(), "10.0.0.2", "   ");

        Assert.DoesNotContain("mesh=", uri);
        Assert.True(PairingCode.TryParse(uri, out var code, out _));
        Assert.Null(code!.MeshName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.com/pair?key=abc")]
    [InlineData("just some text")]
    [InlineData("meshsync://pair")]
    [InlineData("meshsync://pair?ip=192.168.1.10")]
    [InlineData("meshsync://pair?key=not-a-key")]
    [InlineData("meshsync://pair?ip=192.168.1.10&key=")]
    public void Refuses_anything_without_a_usable_key(string raw)
    {
        Assert.False(PairingCode.TryParse(raw, out var code, out string error));

        Assert.Null(code);
        Assert.NotEqual("", error);
    }

    /// <summary>
    /// A QR scanner hands back exactly what was encoded, and a person typing one in does not.
    /// </summary>
    [Fact]
    public void Ignores_surrounding_whitespace()
    {
        string uri = PairingCode.Build(AKey(), "10.0.0.2", null);

        Assert.True(PairingCode.TryParse($"  {uri}\n", out _, out _));
    }

    [Fact]
    public void Scheme_is_matched_without_regard_to_case()
    {
        string uri = PairingCode.Build(AKey(), "10.0.0.2", null);

        Assert.True(PairingCode.TryParse(uri.Replace("meshsync://", "MESHSYNC://"), out _, out _));
    }

    /// <summary>
    /// The wire format has to stay exactly what earlier builds emit, because the phone and the
    /// desktop are updated on different days by different means.
    /// </summary>
    [Fact]
    public void Reads_the_shape_earlier_builds_wrote_by_hand()
    {
        string key = AKey();
        string legacy = $"meshsync://pair?ip={Uri.EscapeDataString("192.168.1.10")}" +
                        $"&key={Uri.EscapeDataString(key)}" +
                        $"&mesh={Uri.EscapeDataString("Surya's Mesh")}";

        Assert.True(PairingCode.TryParse(legacy, out var code, out _));

        Assert.Equal(key, code!.PublicKey);
        Assert.Equal("192.168.1.10", code.Address);
        Assert.Equal("Surya's Mesh", code.MeshName);
    }
}
