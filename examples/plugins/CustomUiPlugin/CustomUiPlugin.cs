using DeadworksManaged.Api;

namespace CustomUiPlugin;

/// <summary>A bundle you can switch to by name with the <c>ui</c> command.</summary>
public class UiVariantConfig
{
	/// <summary>HTTPS URL of the VPK.</summary>
	public string Url { get; set; } = "";

	/// <summary>Lowercase hex SHA-256. Clients reject anything that doesn't match.</summary>
	public string Sha256 { get; set; } = "";
}

public class CustomUiConfig : IConfig
{
	/// <summary>
	/// Publishing the same id again replaces whatever was there before, which
	/// is exactly what variant swapping needs.
	/// </summary>
	public string BundleId { get; set; } = "scoreboard";

	/// <summary>
	/// Panels to reload after mounting, as Panorama cache keys - source path
	/// with backslashes. All the test bundles patch hud_health.
	/// </summary>
	public string[] CacheKeys { get; set; } = [@"panorama\layout\hud_health.xml"];

	/// <summary>Bundles keyed by the name you pass to the <c>ui</c> command.</summary>
	public Dictionary<string, UiVariantConfig> Variants { get; set; } = new();

	public void Validate()
	{
		foreach (var variant in Variants.Values)
			variant.Sha256 = variant.Sha256.Trim().ToLowerInvariant();
	}
}

/// <summary>
/// Internal transport-test harness for the UI content channel - NOT the
/// template for building UI (copy <c>StarterUiPlugin</c> or
/// <c>TreeUiPlugin</c> for that). Swaps raw bundles by name so delivery,
/// live replacement, revoke and - via the <c>corrupt</c> variant - the
/// hash-rejection failure branch can each be exercised on demand.
/// </summary>
/// <remarks>
/// The standard variants live in the plugin config (an edit, not a rebuild):
/// <c>magenta</c> (unmissable pass signal), <c>cyan</c> (live replace),
/// <c>corrupt</c> (truncated VPK claiming the magenta hash - must be
/// REJECTED; the only test of the failure branch, keep it). <c>ui emit</c>
/// probes the data channel without needing a panel action.
/// </remarks>
public class CustomUiPlugin : DeadworksPluginBase
{
	public override string Name => "CustomUiPlugin";

	/// <summary>Reserved name meaning "unmount whatever's there".</summary>
	private const string RevokeVariant = "default";

	/// <summary>Reserved name meaning "push a test value over the data channel".</summary>
	private const string EmitVariant = "emit";

	private int _emitCount;

	[PluginConfig]
	public CustomUiConfig Config { get; set; } = new();

	public override void OnLoad(bool isReload)
	{
		if (Config.Variants.Count == 0)
			Console.WriteLine($"[{Name}] No variants configured; nothing to publish.");
	}

	public override void OnUnload() { }

	/// <summary>
	/// Swaps everyone's custom UI to a named variant, or unmounts it. Applies to
	/// all players rather than the caller - run from the server console there
	/// isn't a caller to target anyway.
	/// </summary>
	[Command("ui", Description = "Swap the server's custom UI: ui <variant|default|emit>")]
	public void CmdUi(CCitadelPlayerController? caller, string variant = "")
	{
		if (string.Equals(variant, RevokeVariant, StringComparison.OrdinalIgnoreCase))
		{
			UI.RevokeBundle(Config.BundleId, RecipientFilter.All);
			Reply(caller, $"[{Name}] revoked '{Config.BundleId}' - panels revert to stock content");
			return;
		}

		// The data-channel probe: any bundle that subscribes to
		// scoreboard.update (the demo bundles do) prints the counter, so the
		// channel can be checked without waiting for a panel action.
		if (string.Equals(variant, EmitVariant, StringComparison.OrdinalIgnoreCase))
		{
			_emitCount++;
			UI.Emit("scoreboard.update", new Dictionary<string, string>
			{
				["reason"] = "manual",
				["n"] = _emitCount.ToString(),
			}, RecipientFilter.All);
			Reply(caller, $"[{Name}] emitted scoreboard.update #{_emitCount} to everyone");
			return;
		}

		if (!Config.Variants.TryGetValue(variant, out var chosen))
		{
			string known = Config.Variants.Count == 0
				? "(none configured)"
				: string.Join(", ", Config.Variants.Keys);
			Reply(caller, string.IsNullOrWhiteSpace(variant)
				? $"[{Name}] usage: ui <variant|{RevokeVariant}|{EmitVariant}>"
				: $"[{Name}] unknown variant '{variant}'");
			Reply(caller, $"[{Name}] available: {known}, {RevokeVariant}, {EmitVariant}");
			return;
		}

		try
		{
			UI.PublishBundle(new UiBundle
			{
				Id = Config.BundleId,
				Url = chosen.Url,
				Sha256 = chosen.Sha256,
				CacheKeys = Config.CacheKeys,
			}, RecipientFilter.All);
			Reply(caller, $"[{Name}] published '{variant}'");
		}
		catch (ArgumentException ex)
		{
			// Bad url or hash in the config. Say so instead of going quiet.
			Reply(caller, $"[{Name}] variant '{variant}' is misconfigured: {ex.Message}");
		}
	}

	/// <summary>Chat if a player ran it, console if the server did.</summary>
	private static void Reply(CCitadelPlayerController? to, string text)
	{
		if (to != null) Chat.PrintToChat(to, text);
		else Console.WriteLine(text);
	}
}
