using CoreLib.Transport;

namespace CoreLib.Tests;

/// <summary>
/// Which links a device is allowed to offer, and the fact that it is remembered.
///
/// <para>The rule is shared; only the shelf differs. Windows keeps the value in the registry and
/// the Linux and Mac head keeps it in a file beside the peer registry, so both are exercised here
/// through a store that does neither.</para>
/// </summary>
public class TransportSettingsTests
{
    private sealed class FakeStore : ITransportPreferenceStore
    {
        public TransportPreference Value = TransportPreference.Both;
        public int Saves;

        public TransportPreference Load() => Value;

        public void Save(TransportPreference preference)
        {
            Value = preference;
            Saves++;
        }
    }

    /// <summary>A device with nowhere to keep a preference offers both links.</summary>
    [Fact]
    public void With_no_store_it_offers_both()
    {
        var settings = new TransportSettings();

        Assert.Equal(TransportPreference.Both, settings.Current);
        Assert.True(settings.AllowsWiFi);
        Assert.True(settings.AllowsBle);
    }

    [Fact]
    public void The_stored_preference_is_restored()
    {
        var store = new FakeStore { Value = TransportPreference.Ble };

        var settings = new TransportSettings(store);

        Assert.Equal(TransportPreference.Ble, settings.Current);
    }

    [Theory]
    [InlineData(TransportPreference.Both, true, true)]
    [InlineData(TransportPreference.WiFi, true, false)]
    [InlineData(TransportPreference.Ble, false, true)]
    public void Each_preference_allows_what_it_says(TransportPreference preference, bool wifi, bool ble)
    {
        var settings = new TransportSettings(new FakeStore { Value = preference });

        Assert.Equal(wifi, settings.AllowsWiFi);
        Assert.Equal(ble, settings.AllowsBle);
    }

    /// <summary>A preference that resets on restart is worse than not having one.</summary>
    [Fact]
    public void Choosing_a_preference_writes_it_down()
    {
        var store = new FakeStore();
        var settings = new TransportSettings(store);

        settings.Set(TransportPreference.WiFi);

        Assert.Equal(TransportPreference.WiFi, store.Value);
        Assert.Equal(1, store.Saves);
    }

    [Fact]
    public void Choosing_a_preference_announces_it()
    {
        var settings = new TransportSettings(new FakeStore());
        TransportPreference? announced = null;
        settings.Changed += p => announced = p;

        settings.Set(TransportPreference.Ble);

        Assert.Equal(TransportPreference.Ble, announced);
    }

    /// <summary>
    /// Choosing what is already chosen does nothing at all.
    ///
    /// The daemon starts and stops transports on this event, so a redundant one would tear a live
    /// link down and build it again for no reason.
    /// </summary>
    [Fact]
    public void Choosing_the_same_preference_changes_nothing()
    {
        var store = new FakeStore();
        var settings = new TransportSettings(store);
        int announced = 0;
        settings.Changed += _ => announced++;

        settings.Set(TransportPreference.Both);

        Assert.Equal(0, announced);
        Assert.Equal(0, store.Saves);
    }

    /// <summary>A store that cannot be read must not stop the device starting.</summary>
    [Fact]
    public void A_store_that_throws_on_load_falls_back_to_both()
    {
        var settings = new TransportSettings(new ThrowingStore());

        Assert.Equal(TransportPreference.Both, settings.Current);
    }

    /// <summary>Nor must one that cannot be written stop the preference taking effect now.</summary>
    [Fact]
    public void A_store_that_throws_on_save_still_applies_the_preference()
    {
        var settings = new TransportSettings(new ThrowingStore());

        settings.Set(TransportPreference.Ble);

        Assert.Equal(TransportPreference.Ble, settings.Current);
        Assert.False(settings.AllowsWiFi);
    }

    /// <summary>A handler that throws is a broken listener, not a failed preference change.</summary>
    [Fact]
    public void A_throwing_listener_does_not_escape()
    {
        var settings = new TransportSettings(new FakeStore());
        settings.Changed += _ => throw new InvalidOperationException("the window is gone");

        settings.Set(TransportPreference.WiFi);

        Assert.Equal(TransportPreference.WiFi, settings.Current);
    }

    private sealed class ThrowingStore : ITransportPreferenceStore
    {
        public TransportPreference Load() => throw new IOException("no shelf");

        public void Save(TransportPreference preference) => throw new IOException("no shelf");
    }
}
