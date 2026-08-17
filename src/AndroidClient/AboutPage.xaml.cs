using System.Reflection;

namespace AndroidClient;

/// <summary>What the app is, and the state of the mesh it belongs to.</summary>
public partial class AboutPage : ContentPage
{
    public AboutPage()
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        Header.RefreshSubtitle();

        VersionLabel.Text = $"Version {AppVersion()}";
        MeshLabel.Text = SyncManager.MeshName;

        int count = SyncManager.Security.Peers.Count;
        DeviceCount.Text = count switch
        {
            0 => "None paired yet",
            1 => "1 other device",
            _ => $"{count} other devices"
        };

        // Named rather than counted: which tiers are carrying traffic is the useful fact, and
        // it is the one thing about this app that is genuinely unusual.
        LinkState.Text = (SyncManager.WiFiConnected, SyncManager.BleConnected) switch
        {
            (true, true) => "Wi-Fi and Bluetooth",
            (true, false) => "Wi-Fi",
            (false, true) => "Bluetooth only - no network needed",
            _ => "Nothing connected"
        };
    }

    private static string AppVersion()
    {
        try
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version == null ? "1.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
        catch
        {
            return "1.0";
        }
    }
}
