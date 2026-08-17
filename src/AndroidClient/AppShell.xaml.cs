namespace AndroidClient;

public partial class AppShell : Shell
{
	public AppShell()
	{
		InitializeComponent();

		// Returning users go straight to the dashboard; the wizard is first-run only.
		// Done after the shell is built so both routes are registered.
		//
		// Paired as well as set up, because those two used to mean the same thing and no
		// longer do. Pairing details now live in the peer registry rather than in the flag the
		// wizard sets, so a device can have finished setup and hold no peers - which the
		// dashboard reports as "not paired" and offers no way out of.
		Loaded += (_, _) =>
		{
			RefreshFlyoutHeader();

			if (SetupPage.IsSetupComplete() && SyncManager.IsPaired)
			{
				Dispatcher.Dispatch(async () =>
				{
					try { await GoToAsync("//dashboard"); }
					catch { /* the setup route is a safe fallback */ }
				});
			}
		};

		// Re-read on every navigation: the name can be changed in Settings or adopted when a
		// device is added, and the drawer is the one place it is always on screen.
		Navigated += (_, _) => RefreshFlyoutHeader();
	}

	private void RefreshFlyoutHeader()
	{
		try
		{
			var peers = SyncManager.Security.Peers;

			FlyoutMeshName.Text = peers.MeshNameOrDefault;
			FlyoutMeshSub.Text = peers.Count switch
			{
				0 => "No devices yet",
				1 => "1 other device",
				_ => $"{peers.Count} other devices"
			};
		}
		catch
		{
			// The drawer is decoration; it must never be the thing that stops the app opening.
		}
	}
}
