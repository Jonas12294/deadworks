namespace DeadworksManaged.Api;

/// <summary>
/// Tracks every live <see cref="UiApp"/> so the host can drive their
/// lifecycle: connect/disconnect events fan out to all apps, and unloading a
/// plugin disposes the apps it created. This is what frees plugins from
/// forwarding <c>OnClientFullConnect</c>/<c>OnClientDisconnect</c> by hand.
/// </summary>
internal static class UiAppRegistry
{
	private static readonly object _lock = new();
	private static readonly List<(IDeadworksPlugin Plugin, UiApp App)> _apps = new();

	internal static void Register(IDeadworksPlugin plugin, UiApp app)
	{
		lock (_lock)
			_apps.Add((plugin, app));
	}

	/// <summary>
	/// Finds the plugin owning a live app with this id, so a second creation
	/// can fail with a message naming the conflict.
	/// </summary>
	internal static bool TryFindOwner(string appId, out string ownerName)
	{
		lock (_lock)
		{
			foreach (var (plugin, app) in _apps)
			{
				if (app.AppId == appId)
				{
					ownerName = plugin.Name;
					return true;
				}
			}
		}
		ownerName = "";
		return false;
	}

	/// <summary>
	/// The app a plugin's action handler should answer through: the one whose
	/// panel is live for that player — that is the panel the action came from —
	/// falling back to the plugin's only app when it has exactly one. Null when
	/// the plugin owns no app, or owns several and none of their panels is live
	/// for that slot (nothing to disambiguate with; the caller says so).
	/// </summary>
	internal static UiApp? FindForPlugin(IDeadworksPlugin plugin, int slot)
	{
		lock (_lock)
		{
			UiApp? sole = null;
			int owned = 0;
			foreach (var (owner, app) in _apps)
			{
				if (!ReferenceEquals(owner, plugin))
					continue;
				if (app.IsPanelLive(slot))
					return app;
				sole = app;
				owned++;
			}
			return owned == 1 ? sole : null;
		}
	}

	/// <summary>Called by <see cref="UiApp.Dispose"/>; unknown apps are a no-op.</summary>
	internal static void Unregister(UiApp app)
	{
		lock (_lock)
			_apps.RemoveAll(entry => ReferenceEquals(entry.App, app));
	}

	/// <summary>Host entry: a player fully connected.</summary>
	internal static void HandleConnect(int slot)
	{
		foreach (var app in Snapshot())
		{
			try
			{
				app.HandleConnect(slot);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[UI] '{app.AppId}' HandleConnect({slot}) threw: {ex}");
			}
		}
	}

	/// <summary>Host entry: a player disconnected.</summary>
	internal static void HandleDisconnect(int slot)
	{
		foreach (var app in Snapshot())
		{
			try
			{
				app.HandleDisconnect(slot);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[UI] '{app.AppId}' HandleDisconnect({slot}) threw: {ex}");
			}
		}
	}

	/// <summary>
	/// Host entry: a plugin is unloading — dispose every app it created (like
	/// the timer service, before the plugin's own <c>OnUnload</c> runs, so a
	/// plugin that forgets to dispose cannot leak handlers).
	/// </summary>
	internal static void DisposePlugin(IDeadworksPlugin plugin)
	{
		List<UiApp> owned;
		lock (_lock)
			owned = _apps.Where(entry => ReferenceEquals(entry.Plugin, plugin))
				.Select(entry => entry.App).ToList();

		foreach (var app in owned)
		{
			try
			{
				app.Dispose();
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[UI] disposing '{app.AppId}' on unload threw: {ex}");
			}
		}
	}

	private static List<UiApp> Snapshot()
	{
		lock (_lock)
			return _apps.Select(entry => entry.App).ToList();
	}
}
