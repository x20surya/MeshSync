namespace AndroidClient;

/// <summary>
/// The heading every page shares: a hamburger that opens the drawer, the page name, and the
/// mesh name beneath it.
///
/// <para>The mesh name sits here rather than only on the dashboard because it is the answer to
/// "what am I looking at" on every page, and because it is the thing the product is now about -
/// a named set of devices rather than a connection to one machine.</para>
/// </summary>
public partial class MeshHeader : ContentView
{
    public static readonly BindableProperty TitleProperty =
        BindableProperty.Create(nameof(Title), typeof(string), typeof(MeshHeader), "",
            propertyChanged: (b, _, v) => ((MeshHeader)b).TitleLabel.Text = (string?)v ?? "");

    /// <summary>
    /// Overrides the line under the title. Left unset it shows the mesh name, which is what
    /// every page wants except the ones explaining something more specific.
    /// </summary>
    public static readonly BindableProperty SubtitleProperty =
        BindableProperty.Create(nameof(Subtitle), typeof(string), typeof(MeshHeader), "",
            propertyChanged: (b, _, _) => ((MeshHeader)b).RefreshSubtitle());

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public MeshHeader()
    {
        InitializeComponent();
        RefreshSubtitle();
    }

    /// <summary>Re-reads the mesh name. Called by pages when they appear, in case it changed.</summary>
    public void RefreshSubtitle()
    {
        if (SubLabel == null) return;

        SubLabel.Text = string.IsNullOrWhiteSpace(Subtitle) ? SyncManager.MeshName : Subtitle;
    }

    private void OnMenuTapped(object? sender, TappedEventArgs e)
    {
        if (Shell.Current != null) Shell.Current.FlyoutIsPresented = true;
    }
}
