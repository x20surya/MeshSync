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

    protected override void OnAppearing()
    {
        base.OnAppearing();
        CheckServiceStatus();
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
            var intent = new Intent(Settings.ActionAccessibilitySettings);
            intent.AddFlags(ActivityFlags.NewTask);
            Android.App.Application.Context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Android] Failed to open settings: {ex.Message}");
        }
#endif
    }
}
