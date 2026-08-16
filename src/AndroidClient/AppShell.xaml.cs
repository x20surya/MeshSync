namespace AndroidClient;

public partial class AppShell : Shell
{
	public AppShell()
	{
		InitializeComponent();

		// Returning users go straight to the dashboard; the wizard is first-run only.
		// Done after the shell is built so both routes are registered.
		Loaded += (_, _) =>
		{
			if (SetupPage.IsSetupComplete())
			{
				Dispatcher.Dispatch(async () =>
				{
					try { await GoToAsync("//dashboard"); }
					catch { /* the setup route is a safe fallback */ }
				});
			}
		};
	}
}
