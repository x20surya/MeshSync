namespace AndroidClient;

#if ANDROID
using Android.Content;
using Android.Provider;
#endif

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        CheckServiceStatus();

        SyncManager.OnConnectionStatusChanged += SyncManager_OnConnectionStatusChanged;
        SyncManager.OnClipboardReceived += SyncManager_OnClipboardReceived;

        // Request Notification Permission (Required for Android 13+)
#if ANDROID
        if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.Tiramisu)
        {
            var status = await Microsoft.Maui.ApplicationModel.Permissions.CheckStatusAsync<Microsoft.Maui.ApplicationModel.Permissions.PostNotifications>();
            if (status != Microsoft.Maui.ApplicationModel.PermissionStatus.Granted)
            {
                await Microsoft.Maui.ApplicationModel.Permissions.RequestAsync<Microsoft.Maui.ApplicationModel.Permissions.PostNotifications>();
            }
        }
#endif

        // Populate fields if saved
        string hostIp = "";
        string hostPubKey = "";
#if ANDROID
        var prefs = Android.App.Application.Context.GetSharedPreferences("SyncPrefs", Android.Content.FileCreationMode.Private);
        hostIp = prefs?.GetString("HostIp", "") ?? "";
        hostPubKey = prefs?.GetString("HostPubKey", "") ?? "";
#endif
        
        if (!string.IsNullOrEmpty(hostIp)) IpAddressEntry.Text = hostIp;
        if (!string.IsNullOrEmpty(hostPubKey)) PairingCodeEntry.Text = hostPubKey;
    }

    private void CheckServiceStatus()
    {
#if ANDROID
        string? enabledServicesSetting = Android.Provider.Settings.Secure.GetString(
            Android.App.Application.Context.ContentResolver, 
            Android.Provider.Settings.Secure.EnabledAccessibilityServices);
            
        bool isActive = enabledServicesSetting != null && enabledServicesSetting.Contains("ClipboardAccessibilityService", StringComparison.OrdinalIgnoreCase);

        if (isActive)
        {
            StatusLabel.Text = "Active & Listening!";
            StatusLabel.TextColor = Colors.Green;
        }
        else
        {
            StatusLabel.Text = $"Waiting... (Debug: {enabledServicesSetting ?? "null"})";
            StatusLabel.TextColor = Colors.Orange;
        }
#else
        StatusLabel.Text = "Not running on Android";
#endif
    }

    private void OnEnableServiceClicked(object sender, EventArgs e)
    {
#if ANDROID
        try 
        {
            var intent = new Android.Content.Intent(Android.Provider.Settings.ActionAccessibilitySettings);
            intent.AddFlags(Android.Content.ActivityFlags.NewTask);
            Android.App.Application.Context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Android] Failed to open settings: {ex.Message}");
        }
#endif
    }

    private async void OnRequestScreenshotClicked(object sender, EventArgs e)
    {
        try 
        {
#if ANDROID
            if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.Tiramisu)
            {
                var status = await Microsoft.Maui.ApplicationModel.Permissions.RequestAsync<Microsoft.Maui.ApplicationModel.Permissions.Photos>();
                if (status == Microsoft.Maui.ApplicationModel.PermissionStatus.Granted)
                {
                    Android.Widget.Toast.MakeText(Android.App.Application.Context, "Screenshot permission granted!", Android.Widget.ToastLength.Short)?.Show();
                }
            }
            else
            {
                var status = await Microsoft.Maui.ApplicationModel.Permissions.RequestAsync<Microsoft.Maui.ApplicationModel.Permissions.StorageRead>();
                if (status == Microsoft.Maui.ApplicationModel.PermissionStatus.Granted)
                {
                    Android.Widget.Toast.MakeText(Android.App.Application.Context, "Screenshot permission granted!", Android.Widget.ToastLength.Short)?.Show();
                }
            }
#endif
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to request permissions: {ex.Message}");
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        SyncManager.OnConnectionStatusChanged -= SyncManager_OnConnectionStatusChanged;
        SyncManager.OnClipboardReceived -= SyncManager_OnClipboardReceived;
    }

    private void SyncManager_OnConnectionStatusChanged(string status)
    {
        MainThread.BeginInvokeOnMainThread(() => {
            NetworkStatusLabel.Text = status;
            if (status.Contains("Connected!"))
                NetworkStatusLabel.TextColor = Colors.Green;
            else if (status.Contains("Failed") || status.Contains("Disconnected"))
                NetworkStatusLabel.TextColor = Colors.Red;
            else
                NetworkStatusLabel.TextColor = Colors.Orange;
        });
    }

    private void SyncManager_OnClipboardReceived(string text)
    {
        MainThread.BeginInvokeOnMainThread(() => {
            ReceivedTextEditor.Text = text;
        });
    }

    private async void OnConnectClicked(object sender, EventArgs e)
    {
        string hostPubKey = PairingCodeEntry.Text?.Trim() ?? "";
        string hostIp = IpAddressEntry.Text?.Trim() ?? "";
        
        if (string.IsNullOrEmpty(hostPubKey) || string.IsNullOrEmpty(hostIp)) return;

        await SyncManager.ConnectAsync(hostIp, hostPubKey);
    }
}
