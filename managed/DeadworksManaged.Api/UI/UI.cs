using System.Text;
using Google.Protobuf;

namespace DeadworksManaged.Api;

/// <summary>
/// Delivers custom Panorama UI to connected clients and exchanges data with it.
/// </summary>
/// <remarks>
/// <para>
/// Content is published as a <see cref="UiBundle"/>: the server sends a small
/// manifest over the game connection and the client fetches the VPK over HTTP,
/// mounts it ahead of the game's own content and reloads the named panels. Only
/// clients running the Deadworks client module act on any of this; everyone else
/// ignores the messages.
/// </para>
/// <para>
/// Players are asked once whether to trust a given server's UI. Until they
/// accept, published bundles are not mounted, so treat custom UI as an
/// enhancement rather than something your mode depends on.
/// </para>
/// </remarks>
public static class UI
{
	// Namespaced so the channel can carry unrelated traffic later without
	// colliding. The shipped client ignores message 148 entirely, so nothing
	// in the base game reacts to any of these.
	private const string EventPublish = "dw.ui.bundle";
	private const string EventRevoke = "dw.ui.revoke";
	private const string EventEmit = "dw.ui.emit";
	private const string EventScript = "dw.ui.script";
	private const string EventPack = "dw.ui.pack";

	/// <summary>
	/// Base64 characters of pack data per <see cref="EventPack"/> chunk. With
	/// the id/hash/counter fields each message stays near the ~1.2 KB budget
	/// the tree codec already proved live on this channel.
	/// </summary>
	internal const int PackChunkChars = 1024;

	/// <summary>
	/// Console command panels send their actions through. Panorama has no binding
	/// that talks to the server directly, but it can dispatch a console command,
	/// and that runs here as the player who sent it — so the caller's slot comes
	/// from the connection it arrived on rather than anything the client claims.
	/// </summary>
	internal const string ActionCommand = "dw_ui_action";

	/// <summary>
	/// Offers a content bundle to the given players and reloads the panels it names.
	/// </summary>
	/// <param name="bundle">The bundle to publish.</param>
	/// <param name="recipients">Which players to send it to.</param>
	/// <exception cref="ArgumentException">The bundle is missing fields, is not https, or has a malformed hash or cache key.</exception>
	public static void PublishBundle(UiBundle bundle, RecipientFilter recipients)
	{
		ArgumentNullException.ThrowIfNull(bundle);
		bundle.Validate();

		var payload = new UiPayload()
			.Add("id", bundle.Id)
			.Add("url", bundle.Url)
			.Add("sha256", bundle.Sha256);

		foreach (var key in bundle.CacheKeys)
			payload.Add("key", key);

		SendToClients(EventPublish, payload, recipients);
	}

	/// <summary>
	/// Unmounts a previously published bundle. Panels it replaced revert to the
	/// game's own content on their next reload.
	/// </summary>
	/// <param name="bundleId">The <see cref="UiBundle.Id"/> to revoke.</param>
	/// <param name="recipients">Which players to unmount it for.</param>
	public static void RevokeBundle(string bundleId, RecipientFilter recipients)
	{
		if (string.IsNullOrWhiteSpace(bundleId))
			throw new ArgumentException("Bundle id is required.", nameof(bundleId));

		SendToClients(EventRevoke, new UiPayload().Add("id", bundleId), recipients);
	}

	/// <summary>
	/// Pushes data to panels listening for <paramref name="eventName"/> via
	/// <c>Deadworks.Subscribe</c> in Panorama script.
	/// </summary>
	/// <param name="eventName">Application-defined event name, e.g. <c>scoreboard.update</c>.</param>
	/// <param name="data">Values to deliver. Keys and values must not contain line breaks.</param>
	/// <param name="recipients">Which players to send to.</param>
	public static void Emit(string eventName, IReadOnlyDictionary<string, string> data, RecipientFilter recipients)
	{
		if (string.IsNullOrWhiteSpace(eventName))
			throw new ArgumentException("Event name is required.", nameof(eventName));
		ArgumentNullException.ThrowIfNull(data);

		var payload = new UiPayload().Add("event", eventName);
		foreach (var (key, value) in data)
			payload.Add("d." + key, value);

		SendToClients(EventEmit, payload, recipients);
	}

	/// <summary>
	/// Runs raw JavaScript on the recipients' clients, natively, in the custom
	/// panel's own V8 context — so it has access to <c>$</c>, the panels, and
	/// the <c>Deadworks</c> helper. This is the one thing Panorama's JS sandbox
	/// forbids from script but the Deadworks client module can do from native
	/// code; without the module it is a silent no-op.
	/// </summary>
	/// <param name="js">JavaScript source to compile and run.</param>
	/// <param name="recipients">Which players to run it on.</param>
	/// <remarks>
	/// The source is base64-framed on the wire, so any content is safe. It runs
	/// once per delivery; there is no return value. Capacity is one usermessage
	/// (a few KB of source); larger scripts should be split or shipped as bundle
	/// content instead. This is a remote-code channel by construction — the same
	/// trust boundary as the rest of server-pushed UI.
	/// </remarks>
	public static void RunScript(string js, RecipientFilter recipients)
	{
		if (string.IsNullOrEmpty(js))
			return;

		string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(js));
		NetMessages.Send(new CUserMsg_CustomGameEvent
		{
			EventName = EventScript,
			Data = ByteString.CopyFromUtf8(b64),
		}, recipients);
	}

	/// <summary>
	/// Delivers a VPK to the recipients over the data channel, chunked, for the
	/// client module (v27+) to reassemble, verify against <paramref name="sha256"/>
	/// and mount as the images pack — no hosting anywhere. Prefer
	/// <see cref="UiApp.ServeImages"/>, which builds the pack from a PNG folder
	/// and owns pushing it; this is the raw sender underneath.
	/// </summary>
	/// <param name="id">Pack id, used by the client to key chunk reassembly.</param>
	/// <param name="vpk">The VPK bytes, e.g. <see cref="UiImagePackResult.Vpk"/>.</param>
	/// <param name="sha256">Hex SHA-256 of <paramref name="vpk"/>; the client refuses a mismatch.</param>
	/// <param name="recipients">Which players to deliver to.</param>
	/// <remarks>
	/// The pack must arrive before any tree that references its images — images
	/// resolve when a tree next uses them, so push the pack on connect, ahead of
	/// state. Without the client module all of this is ignored.
	/// </remarks>
	public static void PushPack(string id, byte[] vpk, string sha256, RecipientFilter recipients)
	{
		if (string.IsNullOrWhiteSpace(id))
			throw new ArgumentException("A pack id is required.", nameof(id));
		ArgumentNullException.ThrowIfNull(vpk);
		if (vpk.Length == 0)
			throw new ArgumentException("The pack VPK is empty.", nameof(vpk));
		if (sha256 is null || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
			throw new ArgumentException("sha256 must be 64 hex characters.", nameof(sha256));
		sha256 = sha256.ToLowerInvariant();

		string b64 = Convert.ToBase64String(vpk);
		int chunks = (b64.Length + PackChunkChars - 1) / PackChunkChars;
		for (int i = 0; i < chunks; i++)
		{
			int at = i * PackChunkChars;
			var payload = new UiPayload()
				.Add("id", id)
				.Add("sha256", sha256)
				.Add("n", chunks.ToString())
				.Add("i", i.ToString())
				.Add("b", b64.Substring(at, Math.Min(PackChunkChars, b64.Length - at)));
			SendToClients(EventPack, payload, recipients);
		}
	}

	/// <summary>
	/// Handles actions sent from Panorama script via <c>Deadworks.SendToServer</c>.
	/// </summary>
	/// <param name="eventName">The event name to listen for.</param>
	/// <param name="handler">Called with the sending player's slot and the delivered values.</param>
	/// <returns>A handle that keeps the subscription alive; call <see cref="IHandle.Cancel"/> to stop listening.</returns>
	/// <remarks>
	/// <para>Values arrive from a client and are therefore untrusted. Validate them
	/// the same way you would any other client input — a player can send whatever
	/// they like here regardless of what your UI offers.</para>
	/// <para>Prefer a <see cref="UiActionHandlerAttribute"/> method for anything
	/// known at compile time; this is the runtime registration path.</para>
	/// </remarks>
	public static IHandle On(string eventName, Action<int, IReadOnlyDictionary<string, string>> handler)
	{
		ValidateEventName(eventName);
		ArgumentNullException.ThrowIfNull(handler);

		return Register(eventName, e =>
		{
			handler(e.PlayerSlot, e.Data);
			return HookResult.Continue;
		});
	}

	/// <summary>
	/// Registers a handler that receives the action as a <see cref="UiAction"/>
	/// and can stop later handlers for the same event by returning
	/// <see cref="HookResult.Stop"/>. This is what
	/// <see cref="UiActionHandlerAttribute"/> methods are bound to.
	/// </summary>
	/// <param name="eventName">The action name to listen for.</param>
	/// <param name="handler">Called with the action.</param>
	/// <param name="owner">
	/// The plugin the handler belongs to, so <see cref="UiAction.App"/> can
	/// resolve to one of the plugin's own apps.
	/// </param>
	/// <param name="app">
	/// The app to answer through, when the registration already knows it
	/// (<see cref="UiApp.On(string, Action{UiAction})"/>); skips resolution.
	/// </param>
	internal static IHandle OnAction(string eventName, Func<UiAction, HookResult> handler,
		IDeadworksPlugin? owner = null, UiApp? app = null)
	{
		ValidateEventName(eventName);
		ArgumentNullException.ThrowIfNull(handler);

		return Register(eventName, handler, owner, app);
	}

	private static void ValidateEventName(string eventName)
	{
		if (string.IsNullOrWhiteSpace(eventName))
			throw new ArgumentException("Event name is required.", nameof(eventName));
		if (eventName[0] == '_')
			throw new ArgumentException(
				$"Event names starting with '_' are reserved for the UI system (got '{eventName}').",
				nameof(eventName));
	}

	/// <summary>
	/// Creates a <see cref="UiApp"/> bound to this plugin — the recommended
	/// way to build UI: it owns publishing, panel presence, state resync and
	/// throttling so the plugin only answers <see cref="UiApp.StateRequested"/>
	/// and its own <see cref="UiApp.On"/> handlers. The host drives the app's
	/// lifecycle from here on: connects, disconnects and plugin unload are
	/// handled automatically, so no forwarding overrides are needed.
	/// </summary>
	/// <param name="plugin">The owning plugin; its per-plugin timer drives resync and throttling.</param>
	/// <param name="options">App settings; <see cref="UiAppOptions.Bundle"/> is validated here.</param>
	public static UiApp CreateApp(IDeadworksPlugin plugin, UiAppOptions options)
	{
		ArgumentNullException.ThrowIfNull(plugin);
		ArgumentNullException.ThrowIfNull(options);
		options.Bundle?.Validate();

		// One live app per id: a second would also answer every _ready for
		// that id and fight the first over the same panel. Fail at load with
		// the owner's name instead of overwriting each other mid-match.
		string? appId = options.AppId ?? options.Bundle?.Id;
		if (appId is not null && UiAppRegistry.TryFindOwner(appId, out string owner))
			throw new InvalidOperationException(
				$"A UiApp with id '{appId}' already exists (created by plugin '{owner}'). " +
				"One live app per id - dispose the existing app, or give this one its own AppId.");

		var app = new UiApp(options, () => TimerResolver.Get(plugin)) { Owner = plugin };
		UiAppRegistry.Register(plugin, app);

		// A plugin (re)loaded mid-match never sees another full-connect for the
		// players already in the server, so creation treats them as connecting
		// now — the bundle reaches them without any isReload bookkeeping.
		for (int slot = 0; slot < Players.MaxSlot; slot++)
			if (Players.IsConnected(slot))
				app.HandleConnect(slot);

		return app;
	}

	/// <summary>
	/// Shows or hides the mouse cursor over the HUD for the given players —
	/// the stock <c>hud_free_cursor</c> toggle the in-game shop uses. Panels
	/// with buttons need it on before anything can be hovered or clicked;
	/// turn it back off when the interaction ends or the player cannot aim.
	/// </summary>
	/// <param name="on">Whether the cursor is free (visible and usable on the HUD).</param>
	/// <param name="recipients">Which players to toggle it for.</param>
	public static void SetFreeCursor(bool on, RecipientFilter recipients)
		=> RunScript(FreeCursorScript(on), recipients);

	/// <summary>The exact dispatch the shop performs; ran in the panel's V8 context.</summary>
	internal static string FreeCursorScript(bool on)
		=> $"$.DispatchEvent(\"CitadelConCommand\", \"hud_free_cursor {(on ? 1 : 0)}\");";

	/// <summary>
	/// Creates a <see cref="UiApp"/> pinned to the officially published host
	/// bundle (<see cref="UiHostBundle"/>) — the zero-config path for
	/// server-built UI: no url, no hash, no cache keys. Everything else works
	/// exactly like <see cref="CreateApp"/>.
	/// </summary>
	/// <param name="plugin">The owning plugin.</param>
	/// <param name="options">
	/// Optional settings. Leave <see cref="UiAppOptions.Bundle"/> unset to use
	/// the pinned host build; set it to publish a custom host build instead.
	/// </param>
	/// <remarks>
	/// <para>Several plugins can each create one. They share the panel — the
	/// host bundle is published once and the trees they build are composed
	/// server-side, each keeping its own <see cref="UiApp.StateRequested"/>,
	/// handlers and images — so two projects can run side by side without
	/// fighting over it. The app id defaults to the plugin's name to keep them
	/// apart; node ids, though, are one namespace per panel, so prefix yours.</para>
	/// </remarks>
	public static UiApp CreateHostApp(IDeadworksPlugin plugin, UiAppOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(plugin);
		options ??= new UiAppOptions();

		// The bundle id is what panels announce and therefore what groups the
		// apps on this panel; the app id has to be this plugin's own, or the
		// second plugin to create one would collide with the first.
		options = new UiAppOptions
		{
			Bundle = options.Bundle ?? UiHostBundle.Create(),
			AppId = options.AppId ?? plugin.Name,
			PublishOnConnect = options.PublishOnConnect,
			ResyncInterval = options.ResyncInterval,
			MinEmitInterval = options.MinEmitInterval,
		};

		return CreateApp(plugin, options);
	}

	/// <summary>
	/// Registration path for reserved (<c>_</c>-prefixed) events — used by
	/// <see cref="UiApp"/> for panel-ready announcements. Not exposed publicly
	/// so plugin code cannot shadow system events.
	/// </summary>
	internal static IHandle OnInternal(string eventName, Action<int, IReadOnlyDictionary<string, string>> handler)
	{
		if (string.IsNullOrWhiteSpace(eventName))
			throw new ArgumentException("Event name is required.", nameof(eventName));
		ArgumentNullException.ThrowIfNull(handler);

		return Register(eventName, e =>
		{
			handler(e.PlayerSlot, e.Data);
			return HookResult.Continue;
		});
	}

	private static IHandle Register(string eventName, Func<UiAction, HookResult> handler,
		IDeadworksPlugin? owner = null, UiApp? app = null)
	{
		var subscription = new Subscription(handler, owner, app);

		lock (_lock)
		{
			if (!_handlers.TryGetValue(eventName, out var list))
			{
				list = new List<Subscription>();
				_handlers[eventName] = list;
			}
			list.Add(subscription);
		}

		return new CallbackHandle(() =>
		{
			lock (_lock)
			{
				if (_handlers.TryGetValue(eventName, out var list))
				{
					list.Remove(subscription);
					if (list.Count == 0)
						_handlers.Remove(eventName);
				}
			}
		});
	}

	// --- internals ---

	/// <summary>
	/// One registered handler and what it can answer through: the owning plugin
	/// (so <see cref="UiAction.App"/> resolves among that plugin's apps) and, when
	/// the registration already knew it, the app itself.
	/// </summary>
	private sealed record Subscription(
		Func<UiAction, HookResult> Invoke, IDeadworksPlugin? Owner, UiApp? App);

	private static readonly object _lock = new();
	private static readonly Dictionary<string, List<Subscription>> _handlers = new();

	private static void SendToClients(string eventName, UiPayload payload, RecipientFilter recipients)
	{
		NetMessages.Send(new CUserMsg_CustomGameEvent
		{
			EventName = eventName,
			Data = ByteString.CopyFrom(payload.ToBytes()),
		}, recipients);
	}

	/// <summary>
	/// Entry point for the <c>dw_ui_action</c> console command, called by the
	/// host once per invocation.
	/// </summary>
	/// <param name="callerSlot">Slot of the player whose connection it arrived on.</param>
	/// <param name="encoded">Base64 <see cref="UiPayload"/> built by the panel.</param>
	/// <remarks>
	/// The argument is arbitrary text typed by a client, so every step of reading
	/// it fails closed and nothing here throws back into the command dispatcher.
	/// </remarks>
	internal static void HandleAction(int callerSlot, string encoded)
	{
		if (string.IsNullOrEmpty(encoded))
			return;

		byte[] data;
		try
		{
			data = Convert.FromBase64String(encoded);
		}
		catch (FormatException)
		{
			return;
		}

		Dispatch(callerSlot, data);
	}

	private static void Dispatch(int playerSlot, ReadOnlySpan<byte> data)
	{
		var entries = UiPayload.Parse(data);
		if (entries is null)
			return;

		string? eventName = UiPayload.Find(entries, "event");
		if (string.IsNullOrEmpty(eventName))
			return;

		List<Subscription>? handlers;
		lock (_lock)
		{
			if (!_handlers.TryGetValue(eventName, out var list) || list.Count == 0)
				return;
			handlers = new List<Subscription>(list);
		}

		var values = new Dictionary<string, string>();
		foreach (var (key, value) in entries)
			if (key.StartsWith("d.", StringComparison.Ordinal))
				values[key[2..]] = value;

		foreach (var subscription in handlers)
		{
			// One action per handler: the values are shared, but what the
			// handler can answer through is the registration's, not the
			// previous handler's.
			var action = new UiAction(eventName, playerSlot, values, subscription.Owner, subscription.App);
			try
			{
				// A handler that reports the action handled owns it - later
				// handlers for the same event do not run.
				if (subscription.Invoke(action) >= HookResult.Stop)
					return;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[UI] handler for '{eventName}' threw: {ex}");
			}
		}
	}
}
