using CoreLib.Identity;
using CoreLib.Transport.Ble;

namespace CoreLib.Tests;

/// <summary>
/// How the mesh discovery key is minted, distributed and converged on.
///
/// <para>There is no coordinator, so the rules have to be ones two devices reach independently.
/// Lowest key wins, compared as 32 unsigned bytes: deterministic, no timestamps, and it converges
/// in one exchange rather than ping-ponging the way every simple rule for the mesh <em>name</em>
/// does.</para>
/// </summary>
public class MeshKeyTests
{
    private static byte[] Key(byte fill) => Enumerable.Repeat(fill, MeshBeacon.KeyLength).ToArray();

    [Fact]
    public void A_fresh_registry_has_no_key_and_advertises_nothing()
    {
        var registry = PeerRegistry.CreateEphemeral();

        Assert.False(registry.HasMeshKey);
        Assert.Null(registry.MeshKey);
        Assert.Empty(MeshBeacon.Build(registry.MeshKey, DateTime.UtcNow));
    }

    [Fact]
    public void Minting_is_idempotent()
    {
        var registry = PeerRegistry.CreateEphemeral();

        var first = registry.MintMeshKeyIfMissing();
        var second = registry.MintMeshKeyIfMissing();

        Assert.Equal(MeshBeacon.KeyLength, first.Length);
        Assert.Equal(first, second);
    }

    /// <summary>Two halves that minted separately converge, and on the same answer.</summary>
    [Fact]
    public void The_lower_key_wins_whichever_side_offers_first()
    {
        var low = Key(0x01);
        var high = Key(0x02);

        var a = PeerRegistry.CreateEphemeral();
        a.AdoptMeshKey(high);
        Assert.True(a.AdoptMeshKey(low));
        Assert.Equal(low, a.MeshKey);

        var b = PeerRegistry.CreateEphemeral();
        b.AdoptMeshKey(low);
        Assert.False(b.AdoptMeshKey(high));
        Assert.Equal(low, b.MeshKey);
    }

    [Fact]
    public void Adopting_the_key_already_held_changes_nothing()
    {
        var registry = PeerRegistry.CreateEphemeral();
        var key = Key(0x05);

        Assert.True(registry.AdoptMeshKey(key));
        Assert.False(registry.AdoptMeshKey(key));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(64)]
    public void A_key_of_the_wrong_length_is_refused(int length)
    {
        var registry = PeerRegistry.CreateEphemeral();

        Assert.False(registry.AdoptMeshKey(new byte[length]));
        Assert.False(registry.HasMeshKey);
    }

    [Fact]
    public void A_null_offer_is_refused()
    {
        var registry = PeerRegistry.CreateEphemeral();

        Assert.False(registry.AdoptMeshKey(null));
    }

    /// <summary>
    /// A version 1 file has no key and must load unchanged, because the upgrade costs no re-pair.
    /// The beacon simply stays off until one is minted or offered.
    /// </summary>
    [Fact]
    public void A_registry_written_before_mesh_keys_existed_still_loads()
    {
        string directory = Path.Combine(Path.GetTempPath(), "meshsync-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var identity = DeviceIdentity.CreateEphemeral();
            File.WriteAllText(Path.Combine(directory, "peers.json"), $$"""
            {
              "Version": 1,
              "MeshName": "Surya's Mesh",
              "Peers": [ { "PublicKey": "{{identity.PublicKey}}", "Name": "S21 FE" } ]
            }
            """);

            var registry = PeerRegistry.LoadOrCreate(directory);

            Assert.Equal("Surya's Mesh", registry.MeshName);
            Assert.Single(registry.Peers);
            Assert.False(registry.HasMeshKey);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void A_minted_key_survives_a_restart()
    {
        string directory = Path.Combine(Path.GetTempPath(), "meshsync-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var first = PeerRegistry.LoadOrCreate(directory);
            var key = first.MintMeshKeyIfMissing();

            var reloaded = PeerRegistry.LoadOrCreate(directory);

            Assert.True(reloaded.HasMeshKey);
            Assert.Equal(key, reloaded.MeshKey);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void A_stored_key_that_will_not_decode_is_ignored_rather_than_fatal()
    {
        string directory = Path.Combine(Path.GetTempPath(), "meshsync-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllText(Path.Combine(directory, "peers.json"),
                """{ "Version": 2, "MeshName": "m", "MeshKey": "not base64 at all !!", "Peers": [] }""");

            var registry = PeerRegistry.LoadOrCreate(directory);

            Assert.False(registry.HasMeshKey);
            Assert.Equal("m", registry.MeshName);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    /// <summary>Handing out a copy, so a caller cannot scribble on the registry's own bytes.</summary>
    [Fact]
    public void The_key_is_handed_out_by_copy()
    {
        var registry = PeerRegistry.CreateEphemeral();
        registry.AdoptMeshKey(Key(0x07));

        var taken = registry.MeshKey!;
        taken[0] = 0xFF;

        Assert.Equal(0x07, registry.MeshKey![0]);
    }
}
