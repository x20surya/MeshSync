using System.Collections.ObjectModel;
using CoreLib;
using CoreLib.Identity;
using CoreLib.Diagnostics;
using CoreLib.Transport;

namespace AndroidClient;

/// <summary>
/// Browsing what another device has shared, and pulling a file out of it.
///
/// <para><b>Three levels, one page.</b> Devices, then that device's shared folders, then what is
/// inside one. A separate page per level would mean three back stacks to keep straight for what
/// is really one list that changes what it is listing.</para>
///
/// <para><b>A folder id is never built here.</b> Ids arrive on the rows of a root listing and are
/// carried forward unchanged; paths below them are assembled from names the far end sent and are
/// checked there before anything is opened. This page has no idea where anything actually is on
/// the other device, which is the point.</para>
/// </summary>
public partial class FilesPage : ContentPage
{
    private readonly ObservableCollection<EntryRow> _rows = new();

    /// <summary>Empty until a device is chosen; then the fingerprint being browsed.</summary>
    private string _peer = "";
    private string _peerName = "";

    /// <summary>Empty while listing a device's shared folders, then the folder being looked in.</summary>
    private string _folderId = "";
    private string _folderName = "";

    /// <summary>Where we are inside that folder. Empty is its top.</summary>
    private string _path = "";

    public FilesPage()
    {
        InitializeComponent();
        EntryList.ItemsSource = _rows;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        ShowDevices();
    }

    // ──────────────────────────────────── levels

    private void ShowDevices()
    {
        _peer = _peerName = _folderId = _folderName = _path = "";

        _rows.Clear();

        var peers = SyncManager.Security.Peers;

        foreach (var peer in peers.Peers.OrderBy(p => p.Name ?? p.Fingerprint))
        {
            _rows.Add(new EntryRow
            {
                Glyph = "▤",
                Name = string.IsNullOrWhiteSpace(peer.Name) ? DeviceIdentity.Shorten(peer.Fingerprint) : peer.Name!,
                Sub = DeviceIdentity.Shorten(peer.Fingerprint),
                Action = "Browse",
                Kind = RowKind.Device,
                Target = peer.Fingerprint
            });
        }

        CrumbDevice.Text = "Files on your devices";
        CrumbPath.Text = _rows.Count == 0 ? "Nothing paired yet" : "Choose a device";
        UpButton.IsVisible = false;
        TruncatedNote.IsVisible = false;
        EmptyLabel.Text = "Pair a device first.";
        EmptyLabel.IsVisible = _rows.Count == 0;
        EntryList.IsVisible = _rows.Count > 0;
    }

    private async Task ShowListingAsync()
    {
        if (!SyncManager.IsConnected)
        {
            await DisplayAlertAsync("Not connected", "That device is not reachable right now.", "OK");
            ShowDevices();
            return;
        }

        Busy.IsRunning = Busy.IsVisible = true;
        EntryList.IsVisible = false;
        EmptyLabel.IsVisible = false;
        TruncatedNote.IsVisible = false;

        try
        {
            var reply = await SyncManager.Browse.BrowseAsync(_peer, _folderId, _path);

            _rows.Clear();

            foreach (var entry in reply.Entries)
            {
                _rows.Add(new EntryRow
                {
                    Glyph = entry.IsDirectory ? "▸" : "⭳",
                    Name = entry.Name,
                    Sub = entry.IsDirectory ? "Folder" : $"{entry.SizeLabel} · {entry.ModifiedUtc.ToLocalTime():d MMM yyyy}",
                    Action = entry.IsDirectory ? "Open" : "Get",
                    Kind = _folderId.Length == 0 ? RowKind.Folder
                         : entry.IsDirectory ? RowKind.Directory
                         : RowKind.File,
                    Target = _folderId.Length == 0 ? entry.Id : entry.Name
                });
            }

            TruncatedNote.IsVisible = reply.Truncated;

            EmptyLabel.Text = reply.Status switch
            {
                BrowseStatus.NotAllowed => "That folder is not shared.",
                BrowseStatus.NoSuchFolder => "That folder is no longer shared.",
                BrowseStatus.NotFound when _folderId.Length == 0 => $"{_peerName} has not shared anything.",
                BrowseStatus.NotFound => "That device did not answer.",
                _ when _folderId.Length == 0 => $"{_peerName} has not shared anything.",
                _ => "This folder is empty."
            };
        }
        catch (Exception ex)
        {
            Log.Write("Browse", "Listing failed", ex);
            _rows.Clear();
            EmptyLabel.Text = "That listing could not be read.";
        }
        finally
        {
            Busy.IsRunning = Busy.IsVisible = false;
            EmptyLabel.IsVisible = _rows.Count == 0;
            EntryList.IsVisible = _rows.Count > 0;
            RenderCrumbs();
        }
    }

    private void RenderCrumbs()
    {
        CrumbDevice.Text = _peerName;
        CrumbPath.Text = _folderId.Length == 0
            ? "Shared folders"
            : _path.Length == 0 ? _folderName : $"{_folderName}/{_path}";

        UpButton.IsVisible = _peer.Length > 0;
    }

    // ──────────────────────────────────── taps

    private async void OnEntryTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Element)?.BindingContext is not EntryRow row) return;

        switch (row.Kind)
        {
            case RowKind.Device:
                _peer = row.Target;
                _peerName = row.Name;
                _folderId = _folderName = _path = "";
                await ShowListingAsync();
                break;

            case RowKind.Folder:
                _folderId = row.Target;
                _folderName = row.Name;
                _path = "";
                await ShowListingAsync();
                break;

            case RowKind.Directory:
                _path = _path.Length == 0 ? row.Target : $"{_path}/{row.Target}";
                await ShowListingAsync();
                break;

            case RowKind.File:
                await FetchAsync(row);
                break;
        }
    }

    /// <summary>
    /// Asks for one file. It arrives by the ordinary transfer path and lands in Downloads like
    /// anything else, so there is nothing to wait on here beyond the request being sent.
    /// </summary>
    private async Task FetchAsync(EntryRow row)
    {
        string relative = _path.Length == 0 ? row.Target : $"{_path}/{row.Target}";

        if (await SyncManager.Browse.FetchAsync(_peer, _folderId, relative))
        {
            await DisplayAlertAsync("Asked for it", $"\"{row.Name}\" will appear in your Downloads.", "OK");
        }
        else
        {
            await DisplayAlertAsync("Not sent", $"\"{row.Name}\" could not be requested just now.", "OK");
        }
    }

    private async void OnUpClicked(object? sender, EventArgs e)
    {
        // One level at a time, back out through the folder to the device list.
        if (_path.Length > 0)
        {
            int cut = _path.LastIndexOf('/');
            _path = cut < 0 ? "" : _path[..cut];
            await ShowListingAsync();
            return;
        }

        if (_folderId.Length > 0)
        {
            _folderId = _folderName = "";
            await ShowListingAsync();
            return;
        }

        ShowDevices();
    }

    // ──────────────────────────────────── rows

    private enum RowKind { Device, Folder, Directory, File }

    private sealed class EntryRow
    {
        public string Glyph { get; init; } = "";
        public string Name { get; init; } = "";
        public string Sub { get; init; } = "";
        public string Action { get; init; } = "";
        public RowKind Kind { get; init; }

        /// <summary>A fingerprint, a folder id, or a single name - whichever the level needs.</summary>
        public string Target { get; init; } = "";
    }
}
