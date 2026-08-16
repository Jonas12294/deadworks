using System.Globalization;

namespace DeadworksManaged.Api;

/// <summary>
/// One action sent by a player's panel (<c>Deadworks.SendToServer</c>), as
/// delivered to a <see cref="UiActionHandlerAttribute"/> method — the UI
/// counterpart of <see cref="GameEvent"/>.
/// </summary>
/// <remarks>
/// <para><see cref="PlayerSlot"/> is authenticated: it comes from the
/// connection the action arrived on, not from anything the client claimed.
/// Everything else was typed by a client and is untrusted — a player can send
/// any field with any value regardless of what your UI offers, so prefer the
/// validating readers (<see cref="OneOf"/>, the clamped <see cref="GetInt(string, int, int, int)"/>,
/// <see cref="Text"/>) over raw <see cref="Data"/>.</para>
/// </remarks>
public sealed class UiAction
{
	private readonly IReadOnlyDictionary<string, string> _data;
	private readonly IDeadworksPlugin? _owner;
	private UiApp? _app;
	private bool _appResolved;

	internal UiAction(string name, int playerSlot, IReadOnlyDictionary<string, string> data,
		IDeadworksPlugin? owner = null, UiApp? app = null)
	{
		Name = name;
		PlayerSlot = playerSlot;
		_data = data;
		_owner = owner;
		_app = app;
		_appResolved = app is not null;
	}

	/// <summary>The action name, e.g. <c>myui.clicked</c>.</summary>
	public string Name { get; }

	/// <summary>The slot of the player whose panel sent this.</summary>
	public int PlayerSlot { get; }

	/// <summary>The sending player's controller, or null if the slot has none.</summary>
	public CCitadelPlayerController? Player => Players.FromSlot(PlayerSlot);

	/// <summary>The raw values, for anything the readers below do not cover.</summary>
	public IReadOnlyDictionary<string, string> Data => _data;

	/// <summary>Whether the panel sent this field at all.</summary>
	public bool Has(string key) => _data.ContainsKey(key);

	/// <summary>Reads a string field, or <paramref name="def"/> when it is missing.</summary>
	public string GetString(string key, string def = "")
		=> _data.TryGetValue(key, out var value) ? value : def;

	/// <summary>Reads an integer field, or <paramref name="def"/> when it is missing or not a number.</summary>
	public int GetInt(string key, int def = 0)
		=> _data.TryGetValue(key, out var raw)
			&& int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
			? value : def;

	/// <summary>
	/// Reads an integer field clamped into [<paramref name="min"/>,
	/// <paramref name="max"/>] — a client can never push a value outside the
	/// range — falling back to <paramref name="fallback"/> when it is missing
	/// or not a number.
	/// </summary>
	public int GetInt(string key, int min, int max, int fallback = 0)
		=> _data.Int(key, min, max, fallback);

	/// <summary>Reads a float field, or <paramref name="def"/> when it is missing or not a number.</summary>
	public float GetFloat(string key, float def = 0f)
		=> _data.TryGetValue(key, out var raw)
			&& float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
			? value : def;

	/// <summary>
	/// Reads a boolean field: <c>1</c>/<c>true</c> and <c>0</c>/<c>false</c>
	/// are recognised, anything else — including a missing key — is
	/// <paramref name="def"/>.
	/// </summary>
	public bool GetBool(string key, bool def = false) => _data.Flag(key, def);

	/// <summary>
	/// Whitelists a string field: the value when it is one of
	/// <paramref name="allowed"/>, else the first entry — so the default is
	/// declared once, in the list, and unknown client input can never leak
	/// through.
	/// </summary>
	public string OneOf(string key, params string[] allowed) => _data.OneOf(key, allowed);

	/// <summary>
	/// Reads free text: trimmed and hard-capped at <paramref name="maxLength"/>
	/// characters (empty when missing), so a client cannot store unbounded
	/// strings in your state.
	/// </summary>
	public string Text(string key, int maxLength = 128) => _data.Text(key, maxLength);

	// --- answering the panel that sent this ---

	/// <summary>
	/// The app this action is answered through — the one whose panel is live
	/// for <see cref="PlayerSlot"/>, else the handler's plugin's only app.
	/// </summary>
	/// <remarks>
	/// Null when the handler's plugin owns no app, or owns several and none of
	/// their panels is live for this player. The methods below say so and do
	/// nothing rather than throwing; reach for <see cref="UiApp"/> directly if
	/// you need to pick between several apps yourself.
	/// </remarks>
	public UiApp? App
	{
		get
		{
			if (!_appResolved)
			{
				_appResolved = true;
				if (_owner is not null)
					_app = UiAppRegistry.FindForPlugin(_owner, PlayerSlot);
			}
			return _app;
		}
	}

	/// <summary>Sends an event back to this player's panel (throttled by the app).</summary>
	public void Emit(string eventName, IReadOnlyDictionary<string, string> data)
		=> Answer(nameof(Emit))?.Emit(PlayerSlot, eventName, data);

	/// <summary>Sends a <see cref="UiTable"/> back to this player's panel.</summary>
	public void EmitTable(string eventName, UiTable table)
		=> Answer(nameof(EmitTable))?.EmitTable(PlayerSlot, eventName, table);

	/// <summary>Changes one node of this player's current tree in place.</summary>
	public void UpdateNode(string nodeId, string? text = null, string? style = null, bool? visible = null)
		=> Answer(nameof(UpdateNode))?.UpdateNode(PlayerSlot, nodeId, text, style, visible);

	/// <summary>Replaces this player's panel tree.</summary>
	public void SetTree(UiNode root) => Answer(nameof(SetTree))?.SetTree(PlayerSlot, root);

	/// <summary>Replaces this player's panel with one built from raw markup and CSS.</summary>
	public void SetMarkup(string xml, string? css = null)
		=> Answer(nameof(SetMarkup))?.SetMarkup(PlayerSlot, xml, css);

	/// <summary>
	/// Runs raw JavaScript in this player's panel V8 context. Needs no app —
	/// it goes straight to the sending player's client.
	/// </summary>
	public void RunScript(string js) => UI.RunScript(js, RecipientFilter.Single(PlayerSlot));

	/// <summary>
	/// Shows or hides this player's mouse cursor over the HUD (the stock
	/// <c>hud_free_cursor</c> toggle). Needs no app.
	/// </summary>
	public void SetFreeCursor(bool on) => UI.SetFreeCursor(on, RecipientFilter.Single(PlayerSlot));

	/// <summary>
	/// The app for a call that needs one, or null having logged why — a
	/// handler must never blow up the dispatcher over a missing app.
	/// </summary>
	private UiApp? Answer(string what)
	{
		var app = App;
		if (app is null)
			Console.WriteLine($"[UI] '{Name}' from slot {PlayerSlot}: {what} needs a UiApp and none resolved" +
				$" for plugin '{_owner?.Name ?? "(none)"}' — create one with UI.CreateApp/CreateHostApp," +
				" or call the app you want directly.");
		return app;
	}
}
