using System.Collections.ObjectModel;
using CoreLib;

namespace AndroidClient;

/// <summary>
/// Everything that has moved this session, at more length than the dashboard shows.
///
/// Nothing here is persisted. The activity log is in memory and dies with the process, because
/// clipboard traffic is deliberately ephemeral - this page reports it, it does not store it.
/// </summary>
public partial class ActivityPage : ContentPage
{
    private readonly ObservableCollection<ActivityRow> _rows = new();
    private IDispatcherTimer? _refresh;

    public ActivityPage()
    {
        InitializeComponent();
        ActivityList.ItemsSource = _rows;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        Header.RefreshSubtitle();
        SyncManager.Activity.Changed += OnActivityChanged;
        Render();

        // Relative timestamps go stale silently otherwise.
        _refresh = Dispatcher.CreateTimer();
        _refresh.Interval = TimeSpan.FromSeconds(5);
        _refresh.Tick += (_, _) => Render();
        _refresh.Start();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        SyncManager.Activity.Changed -= OnActivityChanged;
        _refresh?.Stop();
        _refresh = null;
    }

    private void OnActivityChanged(object? sender, EventArgs e) =>
        MainThread.BeginInvokeOnMainThread(Render);

    private void Render()
    {
        var snapshot = SyncManager.Activity.Snapshot();

        _rows.Clear();
        foreach (var entry in snapshot)
        {
            _rows.Add(new ActivityRow
            {
                Glyph = entry.Kind == SyncItemKind.Image ? "▣" : "⧉",
                Title = string.IsNullOrWhiteSpace(entry.Title) ? "(empty)" : entry.Title,
                Sub = $"{(entry.Direction == SyncDirection.Sent ? "Sent" : "Received")} · {entry.SizeLabel}",
                Age = entry.RelativeAge
            });
        }

        ActivityEmpty.IsVisible = _rows.Count == 0;
        SentCount.Text = SyncManager.Activity.SentCount.ToString();
        ReceivedCount.Text = SyncManager.Activity.ReceivedCount.ToString();
    }

    private sealed class ActivityRow
    {
        public string Glyph { get; init; } = "";
        public string Title { get; init; } = "";
        public string Sub { get; init; } = "";
        public string Age { get; init; } = "";
    }
}
