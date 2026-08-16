using DeadworksManaged.Api;

namespace StarterUiPlugin;

public class StarterUiConfig : IConfig
{
	/// <summary>
	/// Manifest written by build_bundle.py (dist/&lt;name&gt;.manifest.json).
	/// Copy it into this plugin's data folder (managed\plugins\StarterUiPlugin\) and
	/// the whole bundle config is one file drop - no values to paste.
	/// A relative path is resolved against the data folder.
	/// </summary>
	public string ManifestFile { get; set; } = "dwstarter.manifest.json";

	/// <summary>Hand-pasted alternative to the manifest: the HTTPS URL of the built VPK.</summary>
	public string Url { get; set; } = "";

	/// <summary>Lowercase hex SHA-256 of the VPK; used with <see cref="Url"/>.</summary>
	public string Sha256 { get; set; } = "";

	/// <summary>Must match the bundle's name and the APP_ID in the panel script.</summary>
	public string BundleId { get; set; } = "dwstarter";

	/// <summary>The greeting shown in the panel.</summary>
	public string Message { get; set; } = "hello from the server";

	/// <summary>How often live panels are refreshed, in seconds. 0 disables it.</summary>
	public double ResyncSeconds { get; set; } = 5.0;

	public void Validate() => Sha256 = Sha256.Trim().ToLowerInvariant();
}

/// <summary>
/// The smallest useful bundle-based UI plugin, and the template to copy for
/// your own: publishes the starter bundle, greets each live panel with a
/// message and the match clock, and answers the panel's ping. Pairs with the
/// bundle in <c>examples/ui/starter/</c>.
/// </summary>
/// <remarks>
/// There is no lifecycle code here: creating the app is enough. The host
/// publishes to joining players (and to everyone already connected on a
/// plugin reload), tracks which panels are open, throttles emit bursts and
/// disposes the app on unload.
/// </remarks>
public class StarterUiPlugin : DeadworksPluginBase
{
	public override string Name => "StarterUiPlugin";

	[PluginConfig]
	public StarterUiConfig Config { get; set; } = new();

	private UiApp? _app;

	public override void OnLoad(bool isReload)
	{
		UiBundle? bundle = LoadBundle();
		if (bundle is null)
			return;

		_app = UI.CreateApp(this, new UiAppOptions
		{
			Bundle = bundle,
			// Panels can (re)load at any moment; the periodic re-send keeps
			// them current without any bookkeeping here.
			ResyncInterval = Config.ResyncSeconds > 0 ? Config.ResyncSeconds.Seconds() : null,
		});

		// Fires when a panel announces itself (Deadworks.Ready) and on every
		// resync tick - the one place this plugin pushes its state.
		_app.StateRequested += SendHello;
	}

	// The host disposes the app when the plugin unloads.
	public override void OnUnload() => _app = null;

	/// <summary>
	/// What the panel's button sends (<c>Deadworks.SendToServer("starter.ping")</c>).
	/// Attributed methods are registered for you from load to unload, like
	/// <c>[GameEventHandler]</c>. The slot is authenticated; the values are
	/// client input, so validate anything you use.
	/// </summary>
	[UiActionHandler("starter.ping")]
	public void OnPanelPing(UiAction e)
	{
		Chat.PrintToChat(e.PlayerSlot, $"[{Name}] pong - your panel is connected");
		// e.Emit answers the panel that sent this, through this plugin's app.
		e.Emit("starter.hello", Hello());
	}

	/// <summary>
	/// The bundle comes from the build tool's manifest when one is in the
	/// data folder, else from a hand-pasted Url + Sha256 in the config.
	/// </summary>
	private UiBundle? LoadBundle()
	{
		string manifest = Path.IsPathRooted(Config.ManifestFile)
			? Config.ManifestFile
			: Path.Combine(this.GetDataDirectory(), Config.ManifestFile);

		if (File.Exists(manifest))
		{
			try
			{
				return UiBundle.FromManifest(manifest);
			}
			catch (ArgumentException ex)
			{
				Console.WriteLine($"[{Name}] {ex.Message}");
				return null;
			}
		}

		if (!string.IsNullOrWhiteSpace(Config.Url))
			return new UiBundle
			{
				Id = Config.BundleId,
				Url = Config.Url,
				Sha256 = Config.Sha256,
				// CacheKeys defaults to hud_health.xml - the starter's layout.
			};

		Console.WriteLine($"[{Name}] No bundle configured. Run examples/ui/build_bundle.py on the " +
			$"starter bundle, then either copy dist/{Config.ManifestFile} into " +
			"the plugin's data folder or paste the printed Url + Sha256 into the config.");
		return null;
	}

	private void SendHello(int slot) => _app!.Emit(slot, "starter.hello", Hello());

	private Dictionary<string, string> Hello() => new()
	{
		["message"] = Config.Message,
		["time"] = ((int)GameRules.GameClock).ToString(),
	};
}
