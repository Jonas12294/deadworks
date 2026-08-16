namespace DeadworksManaged.Api;

/// <summary>
/// A folder of named UIs served per player, created with
/// <see cref="UiApp.ServeUiLibrary"/>. Each subfolder is one UI —
/// <c>ui.xml</c> (required) plus optional <c>ui.css</c> and <c>ui.js</c> —
/// and every player can be shown a different one (an admin panel for one,
/// the plain HUD for the rest).
/// </summary>
/// <remarks>
/// <para>Everything around the folder is owned here so plugins don't rebuild
/// it: panels that announce themselves get their assigned UI (the
/// <see cref="DefaultUi"/> until told otherwise), edits on disk hot-reload to
/// exactly the players using that UI, a broken edit keeps the last good
/// version on screen, slot reuse cannot leak one player's UI to the next,
/// and the <c>ui.load</c> panel action (a button with
/// <c>onclick="ui.load"</c> and <c>id="&lt;name&gt;"</c>) navigates just the
/// clicking player.</para>
/// <para>UI names double as folder names and arrive from clients via
/// <c>ui.load</c>, so anything that is not a plain folder name is rejected.</para>
/// </remarks>
public sealed class UiLibrary : IHandle
{
	/// <summary>Markup filename inside each UI folder.</summary>
	public const string MarkupFile = "ui.xml";

	/// <summary>Stylesheet filename inside each UI folder (optional).</summary>
	public const string StyleFile = "ui.css";

	/// <summary>Script filename inside each UI folder (optional).</summary>
	public const string ScriptFile = "ui.js";

	/// <summary>The panel action that navigates the sending player: <c>onclick="ui.load"</c>, <c>id="&lt;ui name&gt;"</c>.</summary>
	public const string LoadAction = "ui.load";

	private readonly UiApp _app;
	private readonly string _root;
	private readonly IHandle _poll;
	private readonly IHandle _loadAction;

	/// <summary>Which UI each player is currently shown; null = the default.</summary>
	private readonly string?[] _bySlot = new string?[Players.MaxSlot];

	/// <summary>Last seen write stamp per UI, so edits can be spotted.</summary>
	private readonly Dictionary<string, DateTime> _stamps = new();

	private bool _cancelled;

	/// <summary>The UI shown to players without their own assignment. Set by <see cref="AssignAll"/>.</summary>
	public string DefaultUi { get; set; }

	/// <summary>The UI names available on disk right now.</summary>
	public IReadOnlyList<string> Available
		=> Directory.Exists(_root)
			? Directory.GetDirectories(_root)
				.Where(dir => File.Exists(Path.Combine(dir, MarkupFile)))
				.Select(dir => Path.GetFileName(dir)!)
				.ToArray()
			: Array.Empty<string>();

	internal UiLibrary(UiApp app, ITimer timer, string root, string defaultUi)
	{
		_app = app;
		_root = root;
		DefaultUi = defaultUi;

		app.StateRequested += OnStateRequested;
		app.SlotConnected += OnSlotConnected;
		_loadAction = UI.On(LoadAction, (slot, data) => Assign(slot, data.GetValueOrDefault("id", "")));
		_poll = timer.Every(1.Seconds(), PollFiles);
	}

	/// <summary>The UI a player currently sees: their assignment, else <see cref="DefaultUi"/>.</summary>
	public string UiFor(int slot)
		=> ((uint)slot < Players.MaxSlot ? _bySlot[slot] : null) ?? DefaultUi;

	/// <summary>
	/// Shows one UI to one player, now if their panel is live, and remembers
	/// the choice for reloads. Other players are untouched.
	/// </summary>
	/// <returns><see langword="false"/> when no such UI exists (also the answer for any client-supplied name that isn't a plain folder name).</returns>
	public bool Assign(int slot, string name)
	{
		if (_cancelled || (uint)slot >= Players.MaxSlot)
			return false;

		var ui = Read(name);
		if (ui is null)
			return false;

		_bySlot[slot] = name;
		_stamps[name] = Stamp(name);
		if (_app.IsPanelLive(slot))
			Push(slot, ui, name);
		return true;
	}

	/// <summary>
	/// Makes <paramref name="name"/> the UI for everyone: clears every
	/// per-player assignment, sets <see cref="DefaultUi"/> (so later joiners
	/// get it too) and pushes it to every live panel.
	/// </summary>
	public bool AssignAll(string name)
	{
		if (_cancelled)
			return false;

		var ui = Read(name);
		if (ui is null)
			return false;

		DefaultUi = name;
		Array.Clear(_bySlot);
		_stamps[name] = Stamp(name);
		foreach (int slot in _app.LivePanels)
			Push(slot, ui, name);
		return true;
	}

	/// <summary>Whether <see cref="Cancel"/> has run.</summary>
	public bool IsFinished => _cancelled;

	/// <inheritdoc/>
	public IHandle CancelOnMapChange() => this;

	/// <summary>Stops serving: panels keep whatever they show, nothing further is pushed.</summary>
	public void Cancel()
	{
		if (_cancelled)
			return;
		_cancelled = true;
		_app.StateRequested -= OnStateRequested;
		_app.SlotConnected -= OnSlotConnected;
		_poll.Cancel();
		_loadAction.Cancel();
	}

	// --- internals ---

	private sealed record Content(string Xml, string? Css, string? Js);

	private void OnStateRequested(int slot)
	{
		string name = UiFor(slot);
		var ui = Read(name);
		if (ui is not null)
			Push(slot, ui, name);
		else
			Console.WriteLine($"[UI] '{_app.AppId}' slot {slot}: UI '{name}' not found in {_root}");
	}

	/// <summary>A reused slot must not inherit the previous player's UI.</summary>
	private void OnSlotConnected(int slot) => _bySlot[slot] = null;

	/// <summary>
	/// Reads a UI from disk, or null when it doesn't exist. The name arrives
	/// from clients via <see cref="LoadAction"/>, so anything that could
	/// escape the library folder is treated as absent.
	/// </summary>
	private Content? Read(string name)
	{
		if (string.IsNullOrWhiteSpace(name) || !IsPlainName(name))
			return null;

		string dir = Path.Combine(_root, name);
		string xml = Path.Combine(dir, MarkupFile);
		if (!File.Exists(xml))
			return null;

		try
		{
			string css = Path.Combine(dir, StyleFile);
			string js = Path.Combine(dir, ScriptFile);
			return new Content(
				File.ReadAllText(xml),
				File.Exists(css) ? File.ReadAllText(css) : null,
				File.Exists(js) ? File.ReadAllText(js) : null);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[UI] reading UI '{name}': {ex.Message}");
			return null;
		}
	}

	private static bool IsPlainName(string name)
		=> name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
			&& name.IndexOfAny(['\\', '/']) < 0
			&& name != "." && name != "..";

	/// <summary>Builds and runs one player's UI on their panel. A markup error
	/// is logged and the panel keeps its last good UI.</summary>
	private void Push(int slot, Content ui, string name)
	{
		try
		{
			_app.SetMarkup(slot, ui.Xml, ui.Css);
			// Subscribe only ever appends, so every (re)run first drops the
			// previous script's subscriptions - switching UIs or hot
			// reloading can never stack callbacks. This also covers a UI
			// with no script of its own replacing one that had one.
			_app.RunScript(slot, ui.Js is null
				? UiApp.ResetScript
				: UiApp.ResetScript + "\n" + ui.Js);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[UI] pushing UI '{name}' to slot {slot}: {ex.Message}");
		}
	}

	/// <summary>Newest write time across a UI's files; used to spot edits.</summary>
	private DateTime Stamp(string name)
	{
		string dir = Path.Combine(_root, name);
		DateTime newest = default;
		foreach (var file in new[] { MarkupFile, StyleFile, ScriptFile })
		{
			try
			{
				string path = Path.Combine(dir, file);
				if (File.Exists(path))
				{
					var written = File.GetLastWriteTimeUtc(path);
					if (written > newest)
						newest = written;
				}
			}
			catch { /* transient IO while an editor saves */ }
		}
		return newest;
	}

	/// <summary>
	/// Hot reload: re-push a UI to exactly the players using it when any of
	/// its files change. Only UIs someone is actually looking at are watched.
	/// </summary>
	private void PollFiles()
	{
		var inUse = new HashSet<string>();
		foreach (int slot in _app.LivePanels)
			inUse.Add(UiFor(slot));

		foreach (string name in inUse)
		{
			DateTime stamp = Stamp(name);
			if (_stamps.TryGetValue(name, out var known) && known == stamp)
				continue;
			_stamps[name] = stamp;
			if (known == default)
				continue;   // first sighting, not an edit

			var ui = Read(name);
			if (ui is null)
				continue;
			foreach (int slot in _app.LivePanels)
				if (UiFor(slot) == name)
					Push(slot, ui, name);
			Console.WriteLine($"[UI] '{_app.AppId}' reloaded UI '{name}'");
		}
	}
}
