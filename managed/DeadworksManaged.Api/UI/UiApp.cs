namespace DeadworksManaged.Api;

/// <summary>Settings for a <see cref="UiApp"/>. Set <see cref="Bundle"/>, or <see cref="AppId"/> alone.</summary>
public sealed class UiAppOptions
{
	/// <summary>
	/// The content bundle this app publishes and owns. May be omitted when the
	/// plugin manages publishing itself (variant-swapping test plugins, say) —
	/// then <see cref="AppId"/> is required so presence still routes.
	/// </summary>
	public UiBundle? Bundle { get; init; }

	/// <summary>
	/// The id panels announce with (<c>Deadworks.Ready("&lt;id&gt;")</c>).
	/// Defaults to <see cref="UiBundle.Id"/>; required when <see cref="Bundle"/>
	/// is omitted.
	/// </summary>
	public string? AppId { get; init; }

	/// <summary>
	/// Publish the bundle automatically to each player forwarded through
	/// <see cref="UiApp.HandleConnect"/>. On by default.
	/// </summary>
	public bool PublishOnConnect { get; init; } = true;

	/// <summary>
	/// How often <see cref="UiApp.StateRequested"/> re-fires for every live
	/// panel. Panels can (re)load at any moment with no notice beyond their
	/// ready announcement, so a periodic re-send doubles as state sync.
	/// <see langword="null"/> disables it.
	/// </summary>
	public Duration? ResyncInterval { get; init; }

	/// <summary>
	/// Minimum real time between emits per player and event. Bursts are
	/// coalesced — the latest payload is delivered when the window expires,
	/// never dropped. Must be a real-time duration (<c>Seconds()</c> /
	/// <c>Milliseconds()</c>, not <c>Ticks()</c>). Defaults to 250 ms — the
	/// data channel has no engine-side rate limit, so the throttle is on
	/// unless explicitly disabled with <see langword="null"/>.
	/// </summary>
	public Duration? MinEmitInterval { get; init; } = Duration.FromMilliseconds(250);
}

/// <summary>
/// A plugin's UI session: owns one bundle and everything around it — publish
/// on connect, panel presence, periodic state resync, throttled emits, and
/// action handlers — so a plugin's UI code reduces to answering
/// <see cref="StateRequested"/> and its own <see cref="On"/> handlers.
/// </summary>
/// <remarks>
/// <para>Create with <see cref="UI.CreateApp"/> (or <see cref="UI.CreateHostApp"/>
/// for server-built UI) — that is the whole setup. The host drives the
/// lifecycle: publishing to joiners and to players already connected at
/// creation, presence cleanup on disconnect, and disposal on plugin unload.
/// <see cref="HandleConnect"/>/<see cref="HandleDisconnect"/> remain public
/// for manual control and are safe to call redundantly.</para>
/// <para>Presence comes from the panel calling <c>Deadworks.Ready("&lt;bundle id&gt;")</c>
/// when its script loads. A panel that never announces is treated as absent —
/// which is exactly right for players without the client module.</para>
/// </remarks>
public sealed class UiApp : IDisposable
{
	private readonly UiAppOptions _options;
	private readonly string _appId;

	/// <summary>
	/// What panels announce with. Several apps can share one — every plugin on
	/// the host bundle sees the same <c>dwhost</c> announcement — which is what
	/// lets them share a panel while keeping their own <see cref="AppId"/>.
	/// </summary>
	private readonly string _announceId;

	/// <summary>The apps sharing this panel, which compose their trees together.</summary>
	private readonly UiHostGroup _group;

	/// <summary>This app's contribution to the shared tree, per player.</summary>
	private readonly UiNode?[] _trees = new UiNode?[Players.MaxSlot];
	private readonly Func<ITimer> _timerFactory;
	private readonly EmitThrottle? _throttle;
	private readonly Func<long> _now;
	private readonly bool[] _live = new bool[Players.MaxSlot];

	/// <summary>Whether the bundle went out for the current connection, so a
	/// manual forward on top of the host's automatic dispatch cannot publish twice.</summary>
	private readonly bool[] _publishedThisConnection = new bool[Players.MaxSlot];

	/// <summary>Fired from <see cref="HandleConnect"/> — lets attached helpers
	/// (the UI library) clear per-slot state when a slot is reused.</summary>
	internal event Action<int>? SlotConnected;

	/// <summary>Fired from <see cref="HandleDisconnect"/> — lets attached helpers
	/// (the image server) drop per-slot delivery state.</summary>
	internal event Action<int>? SlotDisconnected;

	/// <summary>The plugin this app was created for; set by <see cref="UI.CreateApp"/>.
	/// Null only for hand-constructed apps in tests.</summary>
	internal IDeadworksPlugin? Owner;

	/// <summary>When each slot last announced, to break announce/push loops.</summary>
	private readonly long[] _lastReadyMs = new long[Players.MaxSlot];
	private readonly int[] _rapidReadies = new int[Players.MaxSlot];

	/// <summary>Announcements closer together than this count as "rapid".</summary>
	private const long RapidReadyMs = 500;

	/// <summary>
	/// How many rapid announcements in a row are served before the app decides
	/// it is in a loop (a state push re-ran a script that called
	/// <c>Deadworks.Ready</c> again, which announces, which pushes...). A real
	/// double reload is served; a runaway is not, so it cannot wedge the server.
	/// </summary>
	private const int RapidReadyLimit = 3;
	private readonly List<IHandle> _handles = new();
	private readonly object _lock = new();
	private ITimer? _timer;
	private bool _disposed;

	/// <summary>The id panels announce with — the bundle id unless overridden.</summary>
	public string AppId => _appId;

	/// <summary>The bundle this app was created with, when it owns one.</summary>
	public UiBundle? Bundle => _options.Bundle;

	/// <summary>
	/// Fired with a player slot whenever that player's panel needs the current
	/// state: when the panel announces itself ready (load or reload), and on
	/// every <see cref="UiAppOptions.ResyncInterval"/> tick for each live
	/// panel. Push your full UI state from here — it is the only callback a
	/// typical app needs. Exceptions are caught and logged per handler.
	/// </summary>
	public event Action<int>? StateRequested;

	internal UiApp(UiAppOptions options, Func<ITimer> timerFactory, Func<long>? clockMs = null)
	{
		_options = options;
		_appId = options.AppId ?? options.Bundle?.Id
			?? throw new ArgumentException("UiAppOptions needs a Bundle or an AppId.");
		if (options.Bundle is null && options.PublishOnConnect)
			throw new ArgumentException("PublishOnConnect needs a Bundle to publish.");
		_timerFactory = timerFactory;

		// The panel announces the bundle it came from, so that is what routes
		// announcements - and what groups the apps sharing that panel.
		_announceId = options.Bundle?.Id ?? _appId;
		_group = UiHostGroup.Join(_announceId, this);

		_now = clockMs ?? (static () => Environment.TickCount64);
		// "Never announced" must not look like a rapid re-announce on a clock
		// that starts at zero.
		Array.Fill(_lastReadyMs, long.MinValue / 2);

		if (options.MinEmitInterval is { } window)
		{
			if (window.Kind != DurationKind.RealTime)
				throw new ArgumentException(
					"MinEmitInterval must be a real-time duration (Seconds()/Milliseconds()), not ticks.");
			_throttle = new EmitThrottle(window.Value, _now);
		}

		_handles.Add(UI.OnInternal("_ready", OnPanelReady));

		if (options.ResyncInterval is { } interval)
			_handles.Add(Timer().Every(interval, ResyncTick));
	}

	// --- lifecycle, forwarded by the plugin ---

	/// <summary>
	/// A player fully connected: clears stale presence for the slot and, when
	/// <see cref="UiAppOptions.PublishOnConnect"/> is on, publishes the bundle
	/// to that player (once per connection — repeated calls are no-ops until
	/// the player disconnects). The host calls this automatically for every
	/// app; plugins only need it when driving the lifecycle by hand.
	/// </summary>
	public void HandleConnect(int slot)
	{
		if ((uint)slot >= Players.MaxSlot)
			return;
		_live[slot] = false;
		SlotConnected?.Invoke(slot);

		// One bundle per panel: whichever app in the group gets here first
		// publishes it, and the others would only be re-sending the same
		// content to the same player.
		if (_options.PublishOnConnect && !_publishedThisConnection[slot] && _group.ClaimPublish(slot, this))
		{
			_publishedThisConnection[slot] = true;
			Publish(RecipientFilter.Single(slot));
		}
	}

	/// <summary>A player disconnected: clears presence. Called automatically by the host.</summary>
	public void HandleDisconnect(int slot)
	{
		if ((uint)slot >= Players.MaxSlot)
			return;
		_live[slot] = false;
		_publishedThisConnection[slot] = false;
		_trees[slot] = null;
		_group.ReleaseSlot(slot);
		SlotDisconnected?.Invoke(slot);
		if (_throttle is not null)
			lock (_lock)
				_throttle.DropSlot(slot);
	}

	// --- presence ---

	/// <summary>Whether this player's panel has announced itself since the player connected.</summary>
	public bool IsPanelLive(int slot) => (uint)slot < Players.MaxSlot && _live[slot];

	/// <summary>Slots whose panels are currently live.</summary>
	public IReadOnlyList<int> LivePanels
	{
		get
		{
			var slots = new List<int>();
			for (int i = 0; i < _live.Length; i++)
				if (_live[i])
					slots.Add(i);
			return slots;
		}
	}

	// --- sending ---

	/// <summary>Sends one event to one player, throttled if configured.</summary>
	public void Emit(int slot, string eventName, IReadOnlyDictionary<string, string> data)
	{
		if (_throttle is null)
		{
			Send(slot, eventName, data);
			return;
		}

		bool sendNow;
		long flushIn;
		lock (_lock)
			sendNow = _throttle.TrySend(slot, eventName, data, out flushIn);

		if (sendNow)
			Send(slot, eventName, data);
		else if (flushIn >= 0)
			Timer().Once(Duration.FromMilliseconds(flushIn), () => FlushPending(slot, eventName));
	}

	/// <summary>Sends a <see cref="UiTable"/> to one player.</summary>
	public void EmitTable(int slot, string eventName, UiTable table)
	{
		ArgumentNullException.ThrowIfNull(table);
		Emit(slot, eventName, table.ToData());
	}

	/// <summary>
	/// Sends one event to every live panel, building the payload per player —
	/// per player because state such as a sort preference usually is.
	/// </summary>
	public void EmitAllLive(string eventName, Func<int, IReadOnlyDictionary<string, string>> dataFor)
	{
		ArgumentNullException.ThrowIfNull(dataFor);
		foreach (int slot in LivePanels)
			Emit(slot, eventName, dataFor(slot));
	}

	// --- server-built trees (rendered by the host bundle, examples/ui/host/) ---

	/// <summary>
	/// Builds a described panel tree on one player's host panel — live,
	/// mid-match, no compiled layout involved. Replaces any previous tree.
	/// </summary>
	/// <remarks>
	/// Large trees ship in several chunks, so this deliberately bypasses
	/// <see cref="UiAppOptions.MinEmitInterval"/> (coalescing would drop
	/// chunks). Push trees from <see cref="StateRequested"/> or on change,
	/// not on a fast loop — use <see cref="UpdateNode"/> for high-frequency
	/// value changes.
	/// </remarks>
	public void SetTree(int slot, UiNode root)
	{
		ArgumentNullException.ThrowIfNull(root);
		if ((uint)slot >= Players.MaxSlot)
			return;

		// The panel holds one container, so what goes out is every app's tree
		// composed - this app only owns its own part of it.
		_trees[slot] = root;
		_group.Push(slot);
	}

	/// <summary>
	/// Takes this app's UI off one player's panel, leaving anything other apps
	/// show on the same panel in place.
	/// </summary>
	public void ClearTree(int slot)
	{
		if ((uint)slot >= Players.MaxSlot || _trees[slot] is null)
			return;
		_trees[slot] = null;
		_group.Push(slot);
	}

	/// <summary>This app's contribution to the shared panel, for the group to compose.</summary>
	internal UiNode? TreeFor(int slot) => (uint)slot < Players.MaxSlot ? _trees[slot] : null;

	/// <summary>Any live subtree, for collision reporting (ids are per app, not per player).</summary>
	internal UiNode? AnyTree()
	{
		foreach (var tree in _trees)
			if (tree is not null)
				return tree;
		return null;
	}

	/// <summary>
	/// The <c>dw://</c> folder this app's images live under, so a bare
	/// <c>dw://logo.png</c> in its trees resolves to its own image rather than
	/// another app's. Null until it serves images.
	/// </summary>
	internal string? ImageNamespace { get; private set; }

	/// <summary>
	/// Changes one node of the current tree in place — text, style and/or
	/// visibility — addressed by the <see cref="UiNode.Id"/> it was built
	/// with. Cheap enough for frequent updates; unknown ids are a logged
	/// no-op on the client.
	/// </summary>
	public void UpdateNode(int slot, string nodeId, string? text = null, string? style = null, bool? visible = null)
	{
		if (string.IsNullOrWhiteSpace(nodeId))
			throw new ArgumentException("A node id is required.", nameof(nodeId));
		if (text is null && style is null && visible is null)
			return;

		// Distinct updates share one event name, so the coalescing throttle
		// would overwrite one node's update with another's; send directly.
		Send(slot, UiTreeCodec.UpdateEvent, UiTreeCodec.EncodeUpdate(nodeId, text, style, visible).ToData());
	}

	/// <summary>
	/// Builds a panel from raw markup and CSS text — the "push raw HTML/CSS"
	/// path. Parsed server-side into a tree (<see cref="UiMarkup"/>) and sent
	/// exactly like <see cref="SetTree"/>.
	/// </summary>
	public void SetMarkup(int slot, string xml, string? css = null)
		=> SetTree(slot, UiMarkup.Parse(xml, css));

	/// <summary>
	/// Runs raw JavaScript on one player's client, natively in the custom
	/// panel's V8 context (see <see cref="UI.RunScript"/>). This is the "push
	/// raw JS" path — behavior does not have to be declarative.
	/// </summary>
	public void RunScript(int slot, string js) => UI.RunScript(js, RecipientFilter.Single(slot));

	/// <summary>
	/// Shows or hides one player's mouse cursor over the HUD — the stock
	/// <c>hud_free_cursor</c> toggle the in-game shop uses. Panels with
	/// buttons need it on before anything can be hovered or clicked; turn it
	/// back off when the interaction ends or the player cannot aim.
	/// (Panel script can do the same itself with <c>Deadworks.FreeCursor(on)</c>.)
	/// </summary>
	public void SetFreeCursor(int slot, bool on)
		=> UI.SetFreeCursor(on, RecipientFilter.Single(slot));

	/// <summary>
	/// Serves a folder of named UIs, one per player (see <see cref="UiLibrary"/>):
	/// each subfolder holds <c>ui.xml</c> + optional <c>ui.css</c>/<c>ui.js</c>,
	/// panels get their assigned UI when they announce, edits hot-reload to
	/// their users, and the <c>ui.load</c> panel action navigates per player.
	/// This is the recommended way to build server-driven UI on the host bundle.
	/// </summary>
	/// <param name="folder">
	/// The library folder. A relative path lives in the plugin's data
	/// directory (<see cref="ConfigExtensions.GetDataDirectory"/>), which is
	/// where server operators should drop UI folders.
	/// </param>
	/// <param name="defaultUi">The UI players see until assigned another.</param>
	/// <returns>The library; <see cref="UiLibrary.Cancel"/> stops serving, as does <see cref="Dispose"/>.</returns>
	public UiLibrary ServeUiLibrary(string folder = "uis", string defaultUi = "default")
	{
		if (string.IsNullOrWhiteSpace(folder))
			throw new ArgumentException("A library folder is required.", nameof(folder));
		if (string.IsNullOrWhiteSpace(defaultUi))
			throw new ArgumentException("A default UI name is required.", nameof(defaultUi));

		string root = Path.IsPathRooted(folder)
			? folder
			: Path.Combine(Owner?.GetDataDirectory() ?? Directory.GetCurrentDirectory(), folder);

		var library = new UiLibrary(this, Timer(), root, defaultUi);
		_handles.Add(library);
		return library;
	}

	/// <summary>
	/// Serves a hot-watched folder of PNGs as in-game images (see
	/// <see cref="UiImageServer"/>): drop <c>name.png</c> into the folder and
	/// any tree or markup can show it as <c>&lt;img src="dw://name.png"&gt;</c>
	/// — the server compiles and packs the textures itself and pushes the pack
	/// over the data channel, so nothing is hosted anywhere. Needs client
	/// module v27+; without it images are silently absent, like all custom UI.
	/// </summary>
	/// <param name="folder">
	/// The images folder. A relative path lives in the plugin's data directory
	/// (<c>managed\plugins\&lt;Name&gt;\images</c> by default), which is where
	/// server operators drop PNGs.
	/// </param>
	/// <returns>The server; <see cref="UiImageServer.Cancel"/> stops serving, as does <see cref="Dispose"/>.</returns>
	public UiImageServer ServeImages(string folder = "images")
	{
		if (string.IsNullOrWhiteSpace(folder))
			throw new ArgumentException("An images folder is required.", nameof(folder));

		string root = Path.IsPathRooted(folder)
			? folder
			: Path.Combine(Owner?.GetDataDirectory() ?? Directory.GetCurrentDirectory(), folder);

		// From here on this app's bare dw:// srcs resolve to its own folder in
		// the merged pack, so two apps' logo.png never collide.
		ImageNamespace = _appId;

		var images = new UiImageServer(this, Timer(), root);
		_handles.Add(images);
		return images;
	}

	/// <summary>
	/// The edit-save-see-it-live loop for a JSON tree file (see
	/// <see cref="UiTreeFile"/>): serves it to every panel that asks for
	/// state, polls it once a second, and re-pushes on change. A file that
	/// fails to parse is logged and the last good tree is kept, so a typo
	/// never blanks the panel.
	/// </summary>
	/// <remarks>
	/// Leave <see cref="UiAppOptions.ResyncInterval"/> unset on apps using a
	/// serve-file loop — every resync would be a full rebuild.
	/// </remarks>
	/// <returns>A handle that stops serving; also cancelled by <see cref="Dispose"/>.</returns>
	public IHandle ServeTreeFile(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new ArgumentException("A tree file path is required.", nameof(path));

		return ServeFiles(new[] { path }, () => UiTreeFile.Load(path), "tree file");
	}

	/// <summary>
	/// The edit-save-see-it-live loop for raw markup: serves
	/// <paramref name="xmlPath"/> (and optional <paramref name="cssPath"/>) as
	/// a live panel, re-parsing and re-pushing whenever either file changes.
	/// A parse error keeps the last good result. This is the "author real
	/// .xml/.css, no compiler, no host" workflow.
	/// </summary>
	/// <returns>A handle that stops serving; also cancelled by <see cref="Dispose"/>.</returns>
	public IHandle ServeMarkupFile(string xmlPath, string? cssPath = null)
	{
		if (string.IsNullOrWhiteSpace(xmlPath))
			throw new ArgumentException("A markup file path is required.", nameof(xmlPath));

		var watched = cssPath is null ? new[] { xmlPath } : new[] { xmlPath, cssPath };
		return ServeFiles(watched, () =>
		{
			string xml = File.ReadAllText(xmlPath);
			string? css = cssPath is not null && File.Exists(cssPath) ? File.ReadAllText(cssPath) : null;
			return UiMarkup.Parse(xml, css);
		}, "markup file");
	}

	/// <summary>The prelude every served script runs behind: <c>Subscribe</c>
	/// only appends, so re-running a script must first drop the previous
	/// run's subscriptions or callbacks stack forever.</summary>
	internal const string ResetScript = "Deadworks.Reset();";

	/// <summary>
	/// The edit-save-see-it-live loop for raw JavaScript: runs
	/// <paramref name="path"/> on every panel that announces itself, and
	/// re-runs it on all live panels whenever the file changes. Behavior can
	/// therefore be authored and hot-edited as a plain <c>.js</c> file, with
	/// no compiler and no hosting. A read error keeps the last good source.
	/// Each run is preceded by <c>Deadworks.Reset()</c> so re-runs never
	/// stack subscriptions.
	/// </summary>
	/// <returns>A handle that stops serving; also cancelled by <see cref="Dispose"/>.</returns>
	public IHandle ServeScriptFile(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new ArgumentException("A script file path is required.", nameof(path));

		string? js = TryReadText(path);
		DateTime lastWrite = SafeLastWrite(path);

		void OnState(int slot)
		{
			if (js is not null)
				RunScript(slot, ResetScript + "\n" + js);
		}
		StateRequested += OnState;

		var poll = Timer().Every(1.Seconds(), () =>
		{
			DateTime write = SafeLastWrite(path);
			if (write == lastWrite)
				return;
			lastWrite = write;

			string? text = TryReadText(path);
			if (text is null)
				return;   // logged; keep the last good source

			js = text;
			foreach (int slot in LivePanels)
				RunScript(slot, ResetScript + "\n" + js);
			Console.WriteLine($"[UI] '{_appId}' reloaded script file '{path}'");
		});

		var handle = new CallbackHandle(() =>
		{
			StateRequested -= OnState;
			poll.Cancel();
		});
		_handles.Add(handle);
		return handle;
	}

	private static string? TryReadText(string path)
	{
		try
		{
			return File.ReadAllText(path);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[UI] script file '{path}' not loaded: {ex.Message}");
			return null;
		}
	}

	/// <summary>
	/// Shared serve-file machinery: load a tree from <paramref name="load"/>,
	/// push it to any panel that announces itself, and re-push to all live
	/// panels whenever any of <paramref name="paths"/> changes on disk.
	/// </summary>
	private IHandle ServeFiles(string[] paths, Func<UiNode> load, string what)
	{
		UiNode? tree = TryLoad(load, paths[0], what);
		var lastWrite = paths.Select(SafeLastWrite).ToArray();

		void OnState(int slot)
		{
			if (tree is not null)
				SetTree(slot, tree);
		}
		StateRequested += OnState;

		var poll = Timer().Every(1.Seconds(), () =>
		{
			bool changed = false;
			for (int i = 0; i < paths.Length; i++)
			{
				DateTime write = SafeLastWrite(paths[i]);
				if (write != lastWrite[i]) { lastWrite[i] = write; changed = true; }
			}
			if (!changed)
				return;

			var parsed = TryLoad(load, paths[0], what);
			if (parsed is null)
				return;   // logged; keep showing the last good result

			tree = parsed;
			foreach (int slot in LivePanels)
				SetTree(slot, tree);
			Console.WriteLine($"[UI] '{_appId}' reloaded {what} '{paths[0]}'");
		});

		var handle = new CallbackHandle(() =>
		{
			StateRequested -= OnState;
			poll.Cancel();
		});
		_handles.Add(handle);
		return handle;
	}

	private static UiNode? TryLoad(Func<UiNode> load, string path, string what)
	{
		try
		{
			return load();
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[UI] {what} '{path}' not loaded: {ex.Message}");
			return null;
		}
	}

	private static DateTime SafeLastWrite(string path)
	{
		try { return File.GetLastWriteTimeUtc(path); }
		catch { return default; }
	}

	// --- receiving ---

	/// <summary>
	/// Handles an action from panels, exactly like <see cref="UI.On"/>, with
	/// the handle tracked so <see cref="Dispose"/> cancels it. Values arrive
	/// from a client and are untrusted — validate them.
	/// </summary>
	/// <remarks>
	/// For handlers known at compile time prefer a
	/// <see cref="UiActionHandlerAttribute"/> method: it needs no registration,
	/// no captured app, and lives for as long as the plugin does. This overload
	/// is for handlers whose event name is only known at runtime.
	/// </remarks>
	public IHandle On(string eventName, Action<int, IReadOnlyDictionary<string, string>> handler)
	{
		var handle = UI.On(eventName, handler);
		_handles.Add(handle);
		return handle;
	}

	/// <summary>
	/// The same, with the action delivered as a <see cref="UiAction"/> bound to
	/// this app — so <c>e.Emit</c>, <c>e.UpdateNode</c> and the rest answer
	/// through this app with no resolution involved.
	/// </summary>
	public IHandle On(string eventName, Action<UiAction> handler)
	{
		ArgumentNullException.ThrowIfNull(handler);

		var handle = UI.OnAction(eventName, e =>
		{
			handler(e);
			return HookResult.Continue;
		}, Owner, this);
		_handles.Add(handle);
		return handle;
	}

	// --- bundle control ---

	/// <summary>Publishes the bundle manually (a console command, say).</summary>
	public void Publish(RecipientFilter recipients)
		=> UI.PublishBundle(_options.Bundle
			?? throw new InvalidOperationException($"UiApp '{_appId}' has no bundle to publish."), recipients);

	/// <summary>
	/// Unmounts the bundle this app publishes; panels revert to stock. On a
	/// shared panel this takes the panel away from every app on it, so prefer
	/// <see cref="ClearTree"/> to remove only your own UI.
	/// </summary>
	public void Revoke(RecipientFilter recipients) => UI.RevokeBundle(_announceId, recipients);

	/// <summary>Cancels every handler and timer this app registered.</summary>
	public void Dispose()
	{
		_disposed = true;
		UiAppRegistry.Unregister(this);
		// Leaves the panel: the apps still on it re-compose without this one,
		// so an unloaded plugin's UI disappears instead of freezing there.
		_group.Leave(this);
		foreach (var handle in _handles)
			handle.Cancel();
		_handles.Clear();
	}

	// --- internals ---

	private ITimer Timer() => _timer ??= _timerFactory();

	private static void Send(int slot, string eventName, IReadOnlyDictionary<string, string> data)
		=> UI.Emit(eventName, data, RecipientFilter.Single(slot));

	private void FlushPending(int slot, string eventName)
	{
		if (_disposed)
			return;

		IReadOnlyDictionary<string, string>? data;
		lock (_lock)
			data = _throttle!.TakePending(slot, eventName);

		// The player may have left while the flush was queued.
		if (data is not null && Players.IsConnected(slot))
			Send(slot, eventName, data);
	}

	private void OnPanelReady(int slot, IReadOnlyDictionary<string, string> data)
	{
		if (_disposed || (uint)slot >= Players.MaxSlot)
			return;
		// Panels announce the bundle they came from, which several apps can
		// share - each of them answers with its own part of the panel.
		if (data.GetValueOrDefault("app") != _announceId)
			return;   // an announcement for some other panel on this server

		_live[slot] = true;

		// Loop breaker. Answering an announce usually means pushing state, and
		// if that state re-runs a script which announces again, the two sides
		// ping-pong until the server wedges. Occasional fast re-announces are
		// legitimate (a panel really can reload twice), so they are served -
		// only a sustained run is treated as a loop and dropped.
		long now = _now();
		long since = now - _lastReadyMs[slot];
		_lastReadyMs[slot] = now;

		if (since < RapidReadyMs)
		{
			if (++_rapidReadies[slot] >= RapidReadyLimit)
			{
				if (_rapidReadies[slot] == RapidReadyLimit)
					Console.WriteLine(
						$"[UI] '{_appId}' slot {slot}: panel announced {RapidReadyLimit}x within " +
						$"{RapidReadyMs}ms - treating as a loop and ignoring further announcements. " +
						"Server-pushed scripts must NOT call Deadworks.Ready(); the host bundle already does.");
				return;
			}
		}
		else
		{
			_rapidReadies[slot] = 0;
		}

		// Install the panel-side helpers before any state goes out: a served
		// script pushed from StateRequested may use them on its first run, and
		// the channel is ordered, so "before" here means before on the client
		// too. A fresh panel means a fresh V8 context, which is exactly when a
		// panel announces - and one install serves every app on that panel.
		if (_group.ClaimRuntime(slot, this))
			RunScript(slot, UiPanelRuntime.Script);

		FireStateRequested(slot);
	}

	private void ResyncTick()
	{
		for (int slot = 0; slot < _live.Length; slot++)
			if (_live[slot])
				FireStateRequested(slot);
	}

	private void FireStateRequested(int slot)
	{
		var handlers = StateRequested;
		if (handlers is null)
			return;

		foreach (Action<int> handler in handlers.GetInvocationList())
		{
			try
			{
				handler(slot);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[UI] StateRequested handler for '{_appId}' threw: {ex}");
			}
		}
	}
}
