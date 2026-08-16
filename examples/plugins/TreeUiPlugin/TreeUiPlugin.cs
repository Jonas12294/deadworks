using System.Numerics;
using DeadworksManaged.Api;

namespace TreeUiPlugin;

public class TreeUiConfig : IConfig
{
	/// <summary>
	/// HTTPS URL of a custom host bundle build. Leave empty to use the SDK's
	/// built-in default (<see cref="UiHostBundle"/>) - the officially
	/// published build, which is the zero-setup path.
	/// </summary>
	public string Url { get; set; } = "";

	/// <summary>Lowercase hex SHA-256 of the custom host bundle; empty with <see cref="Url"/>.</summary>
	public string Sha256 { get; set; } = "";

	/// <summary>Which UI (subfolder of the <c>uis</c> data folder) players see first.</summary>
	public string DefaultUi { get; set; } = "dashboard";

	public void Validate() => Sha256 = Sha256.Trim().ToLowerInvariant();
}

/// <summary>
/// Server-built UI without the CSDK, and the template to copy for your own:
/// the SDK owns the host bundle, the lifecycle and the per-player UI library,
/// so what's left here is game logic - live data pushes and button handlers.
/// </summary>
/// <remarks>
/// UIs live in the plugin's data folder, one subfolder each with
/// <c>ui.xml</c> + optional <c>ui.css</c>/<c>ui.js</c> - copy the samples
/// from <c>examples/plugins/TreeUiPlugin/uis/</c> into
/// <c>managed\plugins\TreeUiPlugin\uis\</c> to start. Editing a file updates every
/// panel showing it, live, mid-match. A panel button with
/// <c>onclick="ui.load"</c> and <c>id="&lt;name&gt;"</c> navigates just the
/// clicking player; the <c>ui</c> chat command does the same from chat.
/// </remarks>
public class TreeUiPlugin : DeadworksPluginBase
{
	public override string Name => "TreeUiPlugin";

	[PluginConfig]
	public TreeUiConfig Config { get; set; } = new();

	private UiApp? _app;
	private UiLibrary? _library;
	private int _pushTick;

	/// <summary>Who has had the server take Tab and the wheel off them (the F7 demo below).</summary>
	private readonly bool[] _tabTaken = new bool[Players.MaxSlot];

	/// <summary>What each player's wheel has scrolled to while the server owns it.</summary>
	private readonly int[] _wheelValue = new int[Players.MaxSlot];

	public override void OnLoad(bool isReload)
	{
		// CreateHostApp pins the officially published host bundle; a custom
		// Url/Sha256 in the config overrides it. Publishing to joiners (and to
		// everyone already connected on a plugin reload) is automatic.
		_app = string.IsNullOrWhiteSpace(Config.Url)
			? UI.CreateHostApp(this)
			: UI.CreateHostApp(this, new UiAppOptions
			{
				Bundle = new UiBundle { Id = UiHostBundle.Id, Url = Config.Url, Sha256 = Config.Sha256 },
			});

		// The library serves uis\<name>\ from the plugin's data folder, per
		// player: state pushes, hot reload, slot cleanup and ui.load
		// navigation are all built in.
		_library = _app.ServeUiLibrary("uis", Config.DefaultUi);
		Console.WriteLine($"[{Name}] UI library: {Path.Combine(this.GetDataDirectory(), "uis")}");

		// Serve images\ from the data folder as in-game textures: drop a PNG
		// there and any UI can show it as <img src="dw://name.png"> - the
		// server compiles and pushes the pack itself, hosted nowhere (client
		// module v27+). The imgdemo UI shows dwsample.png this way.
		_app.ServeImages();
		Console.WriteLine($"[{Name}] images: {Path.Combine(this.GetDataDirectory(), "images")}");

		// Panel buttons are answered by the [UiActionHandler] methods below -
		// nothing to register here.

		// Arbitrary key input. K is a plain listen (the game still gets it);
		// F6 is taken over entirely - the game never sees it. Keys never fire
		// while the player is typing, so chat is safe either way.
		var keys = Input.For(this);
		keys.On(Key.K, e =>
		{
			if (e.Edge == KeyEdge.Down)
				Chat.PrintToChat(e.PlayerSlot,
					$"[{Name}] you pressed K{(e.Modifiers == KeyModifiers.None ? "" : $" (+{e.Modifiers})")}");
		});
		keys.On(Key.F6, KeyPolicy.BlockAndListen, e =>
		{
			if (e.Edge == KeyEdge.Down)
				Chat.PrintToChat(e.PlayerSlot, $"[{Name}] F6 is mine - the game never saw it");
		});

		// Blocking only in certain circumstances: Tab is the game's scoreboard
		// until F7 hands it to the server for that player alone, and F7 again
		// gives it back. SetPolicy is how a block is switched on and off - the
		// client enforces it the instant the key is pressed, so it has to be
		// set BEFORE the press it should affect (a handler cannot block the
		// press it is handling).
		keys.On(Key.Tab, KeyPolicy.Listen, e =>
		{
			if (e.Edge == KeyEdge.Down && _tabTaken[e.PlayerSlot])
				Chat.PrintToChat(e.PlayerSlot, $"[{Name}] Tab is mine right now - press F7 to give it back");
		});
		// The mouse wheel is a key like any other (client module v29+), except
		// that a notch has no release - it only ever reports KeyEdge.Down. Here
		// it is dormant until F7 mode takes it, and then each notch moves a
		// server-side value instead of scrolling the game.
		keys.On(Key.WheelUp, KeyPolicy.Listen, e => OnWheel(e, +1));
		keys.On(Key.WheelDown, KeyPolicy.Listen, e => OnWheel(e, -1));

		keys.On(Key.F7, e =>
		{
			if (e.Edge != KeyEdge.Down)
				return;

			bool take = !_tabTaken[e.PlayerSlot];
			_tabTaken[e.PlayerSlot] = take;

			// One toggle, three keys: Tab and both wheel directions become the
			// server's for this player only, and go back when they toggle off.
			var recipient = RecipientFilter.Single(e.PlayerSlot);
			var policy = take ? KeyPolicy.BlockAndListen : KeyPolicy.Listen;
			keys.SetPolicy(Key.Tab, policy, recipient);
			keys.SetPolicy(Key.WheelUp, policy, recipient);
			keys.SetPolicy(Key.WheelDown, policy, recipient);

			Chat.PrintToChat(e.PlayerSlot, take
				? $"[{Name}] Tab and the wheel are mine now - scroll to move the counter"
				: $"[{Name}] Tab and the wheel are the game's again");
		});

		// Stream live server-authoritative data to open panels every second.
		// This is the point of the dashboard: match clock, player count, the
		// per-player table and a server-side push counter are things the client
		// cannot know without the server sending them.
		Timer.Every(1.Seconds(), PushLiveData);
	}

	// The host disposes the app, the library and the timers when the plugin
	// unloads - nothing to do here.
	public override void OnUnload() { }

	// --- live server data (server -> panel) ---

	private void PushLiveData()
	{
		if (_app is null || _library is null)
			return;
		var live = _app.LivePanels;
		if (live.Count == 0)
			return;

		_pushTick++;
		var players = BuildPlayerTable();
		var stats = new Dictionary<string, string>
		{
			["clock"] = ((int)GameRules.GameClock).ToString(),
			["players"] = players.RowCount.ToString(),
			["tick"] = _pushTick.ToString(),
		};

		foreach (int slot in live)
		{
			// Per-recipient: the same event carries values only this player
			// should see, alongside the shared ones.
			var mine = new Dictionary<string, string>(stats)
			{
				["you"] = Players.FromSlot(slot)?.PlayerName ?? "?",
				["slot"] = slot.ToString(),
				["ui"] = _library.UiFor(slot),
			};
			_app.Emit(slot, "server.stats", mine);
			_app.EmitTable(slot, "server.players", players);
		}
	}

	private static UiTable BuildPlayerTable()
	{
		var table = new UiTable();
		foreach (var c in Players.GetAll())
		{
			try
			{
				var s = c.PlayerDataGlobal;
				table.AddRow(new()
				{
					["name"] = c.PlayerName,
					["team"] = c.TeamNum,
					["k"] = s.PlayerKills,
					["d"] = s.Deaths,
					["nw"] = s.GoldNetWorth,
				});
			}
			catch { /* a player entity mid-teardown; skip the row */ }
		}
		return table;
	}

	private void PushEvent(string text)
	{
		if (_app is null)
			return;
		var data = new Dictionary<string, string>
		{
			["text"] = text,
			["clock"] = ((int)GameRules.GameClock).ToString(),
		};
		foreach (int slot in _app.LivePanels)
			_app.Emit(slot, "server.event", data);
	}

	[GameEventHandler("player_death")]
	public HookResult OnPlayerDeath(GameEvent e)
	{
		PushEvent("a hero died");
		return HookResult.Continue;
	}

	public override void OnClientFullConnect(ClientFullConnectEvent args)
	{
		// No UI forwarding needed - this override exists purely for the demo's
		// own event feed.
		string name = Players.FromSlot(args.Slot)?.PlayerName ?? "a player";
		PushEvent($"{name} joined");
	}

	/// <summary>
	/// One wheel notch. Only acts while this player handed the wheel over with
	/// F7 — otherwise the notch is theirs and the game already scrolled with it.
	/// </summary>
	private void OnWheel(KeyEvent e, int step)
	{
		if (!_tabTaken[e.PlayerSlot])
			return;

		int value = Math.Clamp(_wheelValue[e.PlayerSlot] + step, 0, 100);
		_wheelValue[e.PlayerSlot] = value;
		Chat.PrintToChat(e.PlayerSlot, $"[{Name}] wheel: {value}");
		_app?.UpdateNode(e.PlayerSlot, "msg", text: $"wheel {value}");
	}

	// --- panel actions (panel -> server) ---
	//
	// One method per action a panel can send, discovered by attribute exactly
	// like [GameEventHandler] - no registration, no lambdas, live from load to
	// unload. The slot on the action is authenticated; every value on it was
	// typed by a client, so read them through the validating accessors.

	/// <summary>
	/// The tree/markup samples' button (its OnClick names this event); answer
	/// visibly and poke a node so UpdateNode is exercised.
	/// </summary>
	[UiActionHandler("tree.ping")]
	public void OnTreePing(UiAction e)
	{
		Chat.PrintToChat(e.PlayerSlot, $"[{Name}] pong - you clicked '{e.GetString("id", "?")}'");
		// e.UpdateNode/e.Emit answer the panel that sent this, through this
		// plugin's app - no need to reach for _app.
		e.UpdateNode("msg", text: $"clock {(int)GameRules.GameClock}s · clicks work");
	}

	/// <summary>
	/// The test console's Ping button (ui.js Deadworks.SendToServer). Answer
	/// with a targeted emit the panel's Deadworks.Subscribe("test.pong") shows
	/// - a full panel→server→panel round trip.
	/// </summary>
	[UiActionHandler("test.ping")]
	public void OnTestPing(UiAction e)
	{
		Chat.PrintToChat(e.PlayerSlot, $"[{Name}] test console ping from slot {e.PlayerSlot}");
		e.Emit("test.pong", new Dictionary<string, string>
		{
			["time"] = ((int)GameRules.GameClock).ToString(),
		});
	}

	/// <summary>
	/// Action buttons: every one dispatches the shared "act.do" event and the
	/// clicked button's id names the action, so one handler runs real
	/// server-side effects on the clicking player. <see cref="DoAction"/>
	/// ignores anything it does not know - a client can send any id regardless
	/// of what the panel shows.
	/// </summary>
	[UiActionHandler("act.do")]
	public void OnActionButton(UiAction e) => DoAction(e.PlayerSlot, e.GetString("id"));

	// --- server-side actions (panel button or `!act <what>`) ---

	/// <summary>Chat fallback so the actions are testable without a HUD cursor.</summary>
	[Command("act", Description = "Run a test action on yourself: act <gold|level|launch|reset>")]
	public void CmdAct(CCitadelPlayerController? caller, string what = "")
	{
		if (caller != null)
			DoAction(caller.Slot, what);
	}

	/// <summary>
	/// Runs a real server-side effect on the given player. The action name is
	/// untrusted client input, so anything unknown is ignored.
	/// </summary>
	private void DoAction(int slot, string action)
	{
		var pawn = Players.FromSlot(slot)?.GetHeroPawn();
		if (pawn == null)
		{
			PushEvent($"slot {slot}: no hero to act on");
			return;
		}

		switch (action)
		{
			case "gold":
				pawn.ModifyCurrency(ECurrencyType.EGold, 1000, ECurrencySource.ETreasureChest, forceGain: true);
				PushEvent($"slot {slot} · +1000 gold");
				break;
			case "level":
				pawn.Level += 1;
				PushEvent($"slot {slot} · level {pawn.Level}");
				break;
			case "launch":
				// Velocity only (position/angles omitted → the native call gets
				// null for those), so it's a knock-up, not a teleport.
				pawn.Teleport(velocity: new Vector3(0f, 0f, 900f));
				PushEvent($"slot {slot} · launched");
				break;
			case "reset":
				pawn.ResetHero();
				PushEvent($"slot {slot} · hero reset");
				break;
			default:
				PushEvent($"slot {slot}: unknown action '{action}'");
				break;
		}
	}

	// --- the `ui` chat command, a thin shell over the library ---

	/// <summary>
	/// Loads a named UI. In chat it applies to the caller only; add
	/// <c>all</c> (or run it from the server console) to apply to everyone.
	/// </summary>
	[Command("ui", Description = "Load a UI: ui <name> [all|<slot>]  ·  ui  lists them")]
	public void CmdUi(CCitadelPlayerController? caller, string name = "", string who = "")
	{
		if (_library is null)
			return;

		string list = string.Join(", ", _library.Available);
		if (string.IsNullOrWhiteSpace(name))
		{
			string mine = caller != null ? $"yours '{_library.UiFor(caller.Slot)}' · " : "";
			Reply(caller, $"[{Name}] {mine}available: {(list.Length > 0 ? list : "(none)")} · usage: ui <name> [all|<slot>]");
			return;
		}

		// From the console there is no caller, so default to everyone.
		bool all = string.Equals(who, "all", StringComparison.OrdinalIgnoreCase)
			|| (caller == null && string.IsNullOrWhiteSpace(who));

		bool ok;
		string target;
		if (all)
		{
			ok = _library.AssignAll(name);
			target = "everyone";
		}
		else if (int.TryParse(who, out int slot))
		{
			ok = _library.Assign(slot, name);
			target = $"slot {slot}";
		}
		else if (caller != null)
		{
			ok = _library.Assign(caller.Slot, name);
			target = "you";
		}
		else
		{
			return;
		}

		Reply(caller, ok
			? $"[{Name}] loaded '{name}' for {target}"
			: $"[{Name}] no UI '{name}' · available: {list}");
	}

	private static void Reply(CCitadelPlayerController? to, string text)
	{
		if (to != null) Chat.PrintToChat(to, text);
		else Console.WriteLine(text);
	}
}
