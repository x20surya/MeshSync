using System;
using Android.Bluetooth;
using Android.Content;
using Android.Content.PM;
using CoreLib.Diagnostics;
using CoreLib.Transport;

namespace AndroidClient.Platforms.Android
{
    /// <summary>
    /// What this phone's radio can actually do.
    ///
    /// <para>Scanning is a given on any device with Bluetooth LE. Advertising is not: it is a
    /// hardware capability, and on a device that lacks it
    /// <c>BluetoothAdapter.BluetoothLeAdvertiser</c> is simply null. That is why role
    /// negotiation is capability-first rather than a straight fingerprint comparison - a phone
    /// that cannot advertise has to be the central whatever its fingerprint sorts to, or the
    /// pair agrees on an arrangement neither can carry out.</para>
    ///
    /// <para>Probed once and remembered. It cannot change while the process is running, and
    /// touching the adapter is not free.</para>
    /// </summary>
    public static class BleCapabilities
    {
        private static BleCapability? _cached;

        public static BleCapability Detect()
        {
            if (_cached.HasValue) return _cached.Value;

            var capability = Probe();
            _cached = capability;

            Log.Write("Bluetooth", $"This device can act as: {Describe(capability)}.");
            return capability;
        }

        private static BleCapability Probe()
        {
            try
            {
                var context = global::Android.App.Application.Context;

                if (context.PackageManager?.HasSystemFeature(PackageManager.FeatureBluetoothLe) != true)
                {
                    return BleCapability.None;
                }

                var manager = (BluetoothManager?)context.GetSystemService(Context.BluetoothService);
                var adapter = manager?.Adapter;
                if (adapter == null) return BleCapability.None;

                // Scanning needs nothing beyond the radio.
                var capability = BleCapability.Central;

                // Null on hardware without peripheral support. Deliberately not
                // IsMultipleAdvertisementSupported, which asks a narrower question - whether
                // several advertisements can run at once - and would rule out devices that can
                // manage the single one this needs.
                if (adapter.BluetoothLeAdvertiser != null) capability |= BleCapability.Peripheral;

                return capability;
            }
            catch (Exception ex)
            {
                // Assume the conservative answer rather than none: scanning is what this app
                // has always done, and claiming no radio at all would disable a working tier.
                Log.Write("Bluetooth", "Could not probe the radio's capabilities; assuming scan only.", ex);
                return BleCapability.Central;
            }
        }

        private static string Describe(BleCapability capability) => capability switch
        {
            BleCapability.Both => "central and peripheral",
            BleCapability.Central => "central only",
            BleCapability.Peripheral => "peripheral only",
            _ => "neither"
        };
    }
}
