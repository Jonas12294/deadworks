using DeadworksManaged.Api;
using System.Text;
using System.Text.Json;

namespace AdminTools;

public class AdminToolsPlugin : DeadworksPluginBase {
	public override string Name => "AdminTools";

	[PluginConfig]
	public AdminConfig Config { get; set; } = new();

	private int _soulTarget = 600;
	private readonly HashSet<int> _adminSlots = new();
	private TextWriter? _originalOut;

	private HashSet<ulong> AdminSteamIds => Config.AdminSteamIds;

	private string? _configPath;

	public override void OnLoad(bool isReload) {
		_configPath = this.GetConfigPath();
		SyncAdminSlots();
		if (_originalOut == null) {
			_originalOut = Console.Out;
			Console.SetOut(new AdminForwardingWriter(_originalOut, _adminSlots));
		}
		Console.WriteLine(isReload ? "AdminTools reloaded!" : "AdminTools loaded!");
		Console.WriteLine($"[AdminTools] Config path: {_configPath ?? "default (will be created on first save)"}");
		if (AdminSteamIds.Count > 0)
			Console.WriteLine($"[AdminTools] {AdminSteamIds.Count} persistent admin(s) loaded from config");
	}

	private void SyncAdminSlots() {
		if (AdminSteamIds.Count == 0) return;
		for (int i = 0; i < 32; i++) {
			var c = Players.FromSlot(i);
			if (c != null && AdminSteamIds.Contains(GetSteamId(c)))
				_adminSlots.Add(i);
		}
	}

	public override void OnUnload() {
		Console.WriteLine("AdminTools unloaded!");
		if (_originalOut != null) {
			Console.SetOut(_originalOut);
			_originalOut = null;
		}
	}

	public override void OnClientPutInServer(ClientPutInServerEvent e) {
		if (e.IsBot || e.Xuid == 0) return;
		if (AdminSteamIds.Contains(e.Xuid)) {
			_adminSlots.Add(e.Slot);
			Console.WriteLine($"[AdminTools] Auto-admin: {e.Name} (slot {e.Slot}, steam {e.Xuid})");
		}
	}

	public override void OnClientDisconnect(ClientDisconnectedEvent e) {
		_adminSlots.Remove(e.Slot);
	}

	private static readonly SchemaAccessor<ulong> _steamID = new("CBasePlayerController"u8, "m_steamID"u8);

	private ulong GetSteamId(CCitadelPlayerController controller) {
		return _steamID.Get(controller.Handle);
	}

	private void SaveConfig() {
		try {
			// Resolve config path from SDK: configs/<PluginClassName>/<PluginClassName>.jsonc
			var path = _configPath;
			if (path == null) {
				// Config doesn't exist yet — derive from SDK convention
				var managedDir = Path.GetDirectoryName(typeof(DeadworksPluginBase).Assembly.Location);
				if (string.IsNullOrEmpty(managedDir)) managedDir = AppContext.BaseDirectory;
				var dir = Path.Combine(managedDir!, "..", "configs", nameof(AdminToolsPlugin));
				Directory.CreateDirectory(dir);
				path = Path.Combine(dir, $"{nameof(AdminToolsPlugin)}.jsonc");
			}
			var json = JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true });
			File.WriteAllText(path, json);
			_configPath = path;
		} catch (Exception ex) {
			Console.WriteLine($"[AdminTools] Failed to save config: {ex.Message}");
		}
	}

	private bool IsAuthorized(ConCommandContext ctx) {
		if (ctx.IsServerCommand) return true;
		return _adminSlots.Contains(ctx.CallerSlot);
	}

	private void Log(ConCommandContext ctx, string msg) {
		Console.WriteLine(msg);
		if (!ctx.IsServerCommand)
			Server.ClientCommand(ctx.CallerSlot, $"echo {msg}");
	}

	[ConCommand("soul_amount")]
	public void CmdSoulAmount(ConCommandContext ctx) {
		if (!IsAuthorized(ctx)) return;
		if (ctx.Args.Length < 2 || !int.TryParse(ctx.Args[1], out int amount)) {
			Log(ctx, $"[AdminTools] Current target: {_soulTarget}. Usage: soul_amount <number>");
			return;
		}
		_soulTarget = amount;
		Log(ctx, $"[AdminTools] Soul target set to {_soulTarget}");
	}

	[ConCommand("givesouls")]
	public void CmdGiveSouls(ConCommandContext ctx) {
		if (!IsAuthorized(ctx)) return;

		int? teamFilter = null;
		int? extraAmount = null;

		// Parse optional args: givesouls [teamid] [amount]
		for (int i = 1; i < ctx.Args.Length; i++) {
			if (int.TryParse(ctx.Args[i], out int val)) {
				if (teamFilter == null && (val == 2 || val == 3))
					teamFilter = val;
				else
					extraAmount = val;
			}
		}

		int count = 0;
		foreach (var pawn in Players.GetAllPawns()) {
			if (teamFilter != null && pawn.TeamNum != teamFilter) continue;
			var data = pawn.PlayerData;
			if (data == null) continue;

			if (extraAmount != null) {
				pawn.ModifyCurrency(ECurrencyType.EGold, extraAmount.Value, ECurrencySource.ECheats, silent: true);
				count++;
			} else {
				int currentSouls = data.GoldNetWorth;
				if (currentSouls < _soulTarget) {
					int needed = _soulTarget - currentSouls;
					pawn.ModifyCurrency(ECurrencyType.EGold, needed, ECurrencySource.ECheats, silent: true);
					count++;
				}
			}
		}

		string teamStr = teamFilter != null ? $" on team {teamFilter}" : "";
		if (extraAmount != null)
			Log(ctx, $"[AdminTools] Gave {extraAmount} souls to {count} players{teamStr}");
		else
			Log(ctx, $"[AdminTools] Gave souls (target {_soulTarget}) to {count} players{teamStr}");
	}

	[ConCommand("resetplayer")]
	public void CmdResetPlayer(ConCommandContext ctx) {
		if (!IsAuthorized(ctx)) return;
		if (ctx.Args.Length >= 2) {
			var controller = FindPlayer(ctx.Args[1]);
			if (controller == null) {
				Log(ctx, $"[AdminTools] Player not found: {ctx.Args[1]}");
				return;
			}
			var pawn = controller.GetHeroPawn();
			if (pawn == null) {
				Log(ctx, $"[AdminTools] {controller.PlayerName} has no pawn");
				return;
			}
			pawn.ResetHero(true);
			Log(ctx, $"[AdminTools] Reset {controller.PlayerName}");
		} else {
			foreach (var pawn in Players.GetAllPawns())
				pawn.ResetHero(true);
			Log(ctx, "[AdminTools] Reset all players' heroes");
		}
	}

	[ConCommand("destroy")]
	public void CmdKill(ConCommandContext ctx) {
		if (!IsAuthorized(ctx)) return;
		if (ctx.Args.Length < 2) {
			Log(ctx, "[AdminTools] Usage: destroy <slotid|playername>");
			return;
		}
		var controller = FindPlayer(ctx.Args[1]);
		if (controller == null) {
			Log(ctx, $"[AdminTools] Player not found: {ctx.Args[1]}");
			return;
		}
		var pawn = controller.GetHeroPawn();
		if (pawn == null) {
			Log(ctx, $"[AdminTools] {controller.PlayerName} has no pawn");
			return;
		}
		using var dmg = new CTakeDamageInfo(99999f, attacker: pawn);
		dmg.DamageFlags |= TakeDamageFlags.ForceDeath | TakeDamageFlags.AllowSuicide;
		pawn.TakeDamage(dmg);
		Log(ctx, $"[AdminTools] Killed {controller.PlayerName}");
	}

	[ConCommand("destroy_team")]
	public void CmdDestroyTeam(ConCommandContext ctx) {
		if (!IsAuthorized(ctx)) return;
		if (ctx.Args.Length < 2 || !int.TryParse(ctx.Args[1], out int teamId)) {
			Log(ctx, "[AdminTools] Usage: destroy_team <teamid> (2=Amber, 3=Sapphire)");
			return;
		}
		int count = 0;
		foreach (var pawn in Players.GetAllPawns()) {
			if (pawn.TeamNum != teamId) continue;
			using var dmg = new CTakeDamageInfo(99999f, attacker: pawn);
			dmg.DamageFlags |= TakeDamageFlags.ForceDeath | TakeDamageFlags.AllowSuicide;
			pawn.TakeDamage(dmg);
			count++;
		}
		Log(ctx, $"[AdminTools] Killed {count} players on team {teamId}");
	}

	[ConCommand("destroy_objectives")]
	public void CmdDestroyObjectives(ConCommandContext ctx) {
		if (!IsAuthorized(ctx)) return;
		string[] designerNames = [
			"npc_boss_tier2",
			"npc_boss_tier3",
			"npc_trooper_boss",
			"npc_barrack_boss",
		];
		int count = 0;
		foreach (var name in designerNames) {
			foreach (var ent in Entities.ByDesignerName(name)) {
				// Strip invulnerability by disabling key modifier states
				var mod = ent.ModifierProp;
				if (mod != null) {
					mod.SetModifierState(EModifierState.Invulnerable, false);
					mod.SetModifierState(EModifierState.TechInvulnerable, false);
					mod.SetModifierState(EModifierState.TechDamageInvulnerable, false);
					mod.SetModifierState(EModifierState.BulletInvulnerable, false);
				}
				// Set health to 0 and force kill through every method
				ent.Health = 0;
				ent.LifeState = LifeState.Dead;
				using var dmg = new CTakeDamageInfo(999999f, attacker: ent);
				dmg.DamageFlags |= TakeDamageFlags.ForceDeath | TakeDamageFlags.AllowSuicide;
				ent.TakeDamage(dmg);
				ent.AcceptInput("Kill");
				count++;
			}
		}
		Log(ctx, $"[AdminTools] Destroyed {count} guardians/walkers");
	}

	[ConCommand("bring")]
	public void CmdBring(ConCommandContext ctx) {
		if (!IsAuthorized(ctx)) return;
		if (ctx.IsServerCommand) {
			Log(ctx, "[AdminTools] bring must be run by a player");
			return;
		}
		var callerPawn = ctx.Controller?.GetHeroPawn();
		if (callerPawn == null) {
			Log(ctx, "[AdminTools] You have no pawn");
			return;
		}
		var dest = callerPawn.Position;
		if (ctx.Args.Length >= 2) {
			var target = FindPlayer(ctx.Args[1]);
			if (target == null) {
				Log(ctx, $"[AdminTools] Player not found: {ctx.Args[1]}");
				return;
			}
			var pawn = target.GetHeroPawn();
			if (pawn == null) {
				Log(ctx, $"[AdminTools] {target.PlayerName} has no pawn");
				return;
			}
			pawn.Teleport(position: dest);
			Log(ctx, $"[AdminTools] Brought {target.PlayerName} to you");
		} else {
			int count = 0;
			foreach (var pawn in Players.GetAllPawns()) {
				if (pawn.Handle == callerPawn.Handle) continue;
				pawn.Teleport(position: dest);
				count++;
			}
			Log(ctx, $"[AdminTools] Brought {count} players to you");
		}
	}

	[ConCommand("setteam")]
	public void CmdSetTeam(ConCommandContext ctx) {
		if (!IsAuthorized(ctx)) return;
		if (ctx.Args.Length < 3) {
			Log(ctx, "[AdminTools] Usage: setteam <slotid|playername> <teamnumber>");
			return;
		}
		var controller = FindPlayer(ctx.Args[1]);
		if (controller == null) {
			Log(ctx, $"[AdminTools] Player not found: {ctx.Args[1]}");
			return;
		}
		if (!int.TryParse(ctx.Args[2], out int teamNum)) {
			Log(ctx, "[AdminTools] Invalid team number");
			return;
		}
		controller.ChangeTeam(teamNum);
		Log(ctx, $"[AdminTools] Moved {controller.PlayerName} to team {teamNum}");
	}

	[ConCommand("giveitem")]
	public void CmdGiveItem(ConCommandContext ctx) {
		if (!IsAuthorized(ctx)) return;
		if (ctx.Args.Length < 2) {
			Log(ctx, "[AdminTools] Usage: giveitem [slotid|playername] <internal_item_name>");
			return;
		}

		string itemName;
		CCitadelPlayerController? target = null;

		if (ctx.Args.Length >= 3) {
			target = FindPlayer(ctx.Args[1]);
			if (target == null) {
				Log(ctx, $"[AdminTools] Player not found: {ctx.Args[1]}");
				return;
			}
			itemName = ctx.Args[2];
		} else {
			itemName = ctx.Args[1];
		}

		// If run by a player (not server) and no target specified, give to self only
		if (target == null && !ctx.IsServerCommand) {
			target = ctx.Controller;
		}

		if (target != null) {
			var pawn = target.GetHeroPawn();
			if (pawn == null) {
				Log(ctx, $"[AdminTools] {target.PlayerName} has no pawn");
				return;
			}
			var ability = pawn.AddAbility(itemName, 0, 0);
			if (ability == null) {
				Log(ctx, $"[AdminTools] Failed to give '{itemName}' to {target.PlayerName} (invalid item?)");
				return;
			}
			Log(ctx, $"[AdminTools] Gave '{itemName}' to {target.PlayerName}");
		} else {
			int count = 0;
			foreach (var pawn in Players.GetAllPawns()) {
				var ability = pawn.AddAbility(itemName, 0, 0);
				if (ability != null) count++;
			}
			Log(ctx, $"[AdminTools] Gave '{itemName}' to {count} players");
		}
	}

	[ConCommand("giveteam")]
	public void CmdGiveTeam(ConCommandContext ctx) {
		if (!IsAuthorized(ctx)) return;
		if (ctx.Args.Length < 3 || !int.TryParse(ctx.Args[1], out int teamId)) {
			Log(ctx, "[AdminTools] Usage: giveteam <teamid> <internal_item_name> (2=Amber, 3=Sapphire)");
			return;
		}
		string itemName = ctx.Args[2];
		int count = 0;
		foreach (var pawn in Players.GetAllPawns()) {
			if (pawn.TeamNum != teamId) continue;
			if (pawn.AddAbility(itemName, 0, 0) != null) count++;
		}
		Log(ctx, $"[AdminTools] Gave '{itemName}' to {count} players on team {teamId}");
	}

	[ConCommand("removeteam")]
	public void CmdRemoveTeam(ConCommandContext ctx) {
		if (!IsAuthorized(ctx)) return;
		if (ctx.Args.Length < 3 || !int.TryParse(ctx.Args[1], out int teamId)) {
			Log(ctx, "[AdminTools] Usage: removeteam <teamid> <internal_item_name> (2=Amber, 3=Sapphire)");
			return;
		}
		string itemName = ctx.Args[2];
		int count = 0;
		foreach (var pawn in Players.GetAllPawns()) {
			if (pawn.TeamNum != teamId) continue;
			if (pawn.RemoveAbility(itemName)) count++;
		}
		Log(ctx, $"[AdminTools] Removed '{itemName}' from {count} players on team {teamId}");
	}

	[ConCommand("respawnplayer")]
	public void CmdRespawnPlayer(ConCommandContext ctx) {
		if (!IsAuthorized(ctx)) return;
		if (ctx.Args.Length < 2) {
			Log(ctx, "[AdminTools] Usage: respawnplayer <slotid|playername>");
			return;
		}
		var controller = FindPlayer(ctx.Args[1]);
		if (controller == null) {
			Log(ctx, $"[AdminTools] Player not found: {ctx.Args[1]}");
			return;
		}
		int slot = FindSlot(controller);
		if (slot < 0) { Log(ctx, "[AdminTools] Could not find slot"); return; }
		Server.ClientCommand(slot, "respawn");
		Log(ctx, $"[AdminTools] Respawned {controller.PlayerName}");
	}

	[ConCommand("respawnteam")]
	public void CmdRespawnTeam(ConCommandContext ctx) {
		if (!IsAuthorized(ctx)) return;
		if (ctx.Args.Length < 2 || !int.TryParse(ctx.Args[1], out int teamId)) {
			Log(ctx, "[AdminTools] Usage: respawnteam <teamid> (2=Amber, 3=Sapphire)");
			return;
		}
		int count = 0;
		for (int i = 0; i < 32; i++) {
			var c = Players.FromSlot(i);
			var pawn = c?.GetHeroPawn();
			if (pawn == null || pawn.TeamNum != teamId) continue;
			if (pawn.Health <= 0) {
				Server.ClientCommand(i, "respawn");
				count++;
			}
		}
		Log(ctx, $"[AdminTools] Respawned {count} dead players on team {teamId}");
	}

	[ConCommand("removeitem")]
	public void CmdRemoveItem(ConCommandContext ctx) {
		if (!IsAuthorized(ctx)) return;
		if (ctx.Args.Length < 2) {
			Log(ctx, "[AdminTools] Usage: removeitem [slotid|playername] <internal_item_name>");
			return;
		}

		string itemName;
		CCitadelPlayerController? target = null;

		if (ctx.Args.Length >= 3) {
			target = FindPlayer(ctx.Args[1]);
			if (target == null) {
				Log(ctx, $"[AdminTools] Player not found: {ctx.Args[1]}");
				return;
			}
			itemName = ctx.Args[2];
		} else {
			itemName = ctx.Args[1];
		}

		// If run by a player (not server) and no target specified, remove from self only
		if (target == null && !ctx.IsServerCommand) {
			target = ctx.Controller;
		}

		if (target != null) {
			var pawn = target.GetHeroPawn();
			if (pawn == null) {
				Log(ctx, $"[AdminTools] {target.PlayerName} has no pawn");
				return;
			}
			if (pawn.RemoveAbility(itemName))
				Log(ctx, $"[AdminTools] Removed '{itemName}' from {target.PlayerName}");
			else
				Log(ctx, $"[AdminTools] '{itemName}' not found on {target.PlayerName}");
		} else {
			int count = 0;
			foreach (var pawn in Players.GetAllPawns()) {
				if (pawn.RemoveAbility(itemName)) count++;
			}
			Log(ctx, $"[AdminTools] Removed '{itemName}' from {count} players");
		}
	}

	[ConCommand("admin_help")]
	public void CmdAdminHelp(ConCommandContext ctx) {
		if (!IsAuthorized(ctx)) return;
		var cmds = new (string cmd, string desc)[] {
			("setadmin <slotid>",                       "Grant admin to a player (server only)"),
			("removeadmin <slotid>",                    "Remove admin from a player (server only)"),
			("setdefaultadmin <steam|slot|name>",       "Add a persistent admin (server only)"),
			("removedefaultadmin <steam|slot|name>",    "Remove a persistent admin (server only)"),
			("listadmins",                              "List all persistent and session admins"),
			("setteam <slot|name> <teamnumber>",        "Move a player to a team (2=Amber, 3=Sapphire)"),
			("resetplayer [slot|name]",                 "Reset a player's hero (or all if no arg)"),
			("destroy <slot|name>",                     "Kill a player"),
			("destroy_team <teamid>",                   "Kill all players on a team (2=Amber, 3=Sapphire)"),
			("destroy_objectives",                      "Destroy all guardians and walkers"),
			("bring [slot|name]",                       "Bring a player (or all) to you"),
			("giveitem [slot|name] <item>",             "Give an item (self if player, all if server)"),
			("removeitem [slot|name] <item>",           "Remove an item (self if player, all if server)"),
			("giveteam <teamid> <item>",                "Give an item to a team (2=Amber, 3=Sapphire)"),
			("removeteam <teamid> <item>",              "Remove an item from a team (2=Amber, 3=Sapphire)"),
			("givesouls [teamid] [amount]",             "Give souls (to target, or extra amount to team/all)"),
			("soul_amount <number>",                    "Set the soul target (current: " + _soulTarget + ")"),
			("respawnplayer <slot|name>",                "Respawn a dead player"),
			("respawnteam <teamid>",                    "Respawn all dead players on a team"),
			("run <command> [args...]",                 "Execute a server command (e.g. run sv_cheats true)"),
			("admin_help",                              "Show this help message"),
			("",                                        "Admins receive all server console output"),
		};
		int pad = 0;
		foreach (var c in cmds)
			if (c.cmd.Length > pad) pad = c.cmd.Length;
		var lines = new List<string> { "=== AdminTools Commands ===" };
		foreach (var c in cmds)
			lines.Add($"  {c.cmd.PadRight(pad)}  -  {c.desc}");
		lines.Add("===========================");
		foreach (var line in lines)
			Log(ctx, line);
	}

	[ConCommand("setadmin")]
	public void CmdSetAdmin(ConCommandContext ctx) {
		if (!ctx.IsServerCommand) return;
		if (ctx.Args.Length < 2 || !int.TryParse(ctx.Args[1], out int slot)) {
			Console.WriteLine("[AdminTools] Usage: setadmin <slotid>");
			return;
		}
		var controller = Players.FromSlot(slot);
		if (controller == null) {
			Console.WriteLine($"[AdminTools] No player in slot {slot}");
			return;
		}
		if (_adminSlots.Add(slot))
			Console.WriteLine($"[AdminTools] {controller.PlayerName} (slot {slot}) is now an admin");
		else
			Console.WriteLine($"[AdminTools] {controller.PlayerName} (slot {slot}) is already an admin");
	}

	[ConCommand("removeadmin")]
	public void CmdRemoveAdmin(ConCommandContext ctx) {
		if (!ctx.IsServerCommand) return;
		if (ctx.Args.Length < 2 || !int.TryParse(ctx.Args[1], out int slot)) {
			Console.WriteLine("[AdminTools] Usage: removeadmin <slotid>");
			return;
		}
		if (_adminSlots.Remove(slot))
			Console.WriteLine($"[AdminTools] Removed admin from slot {slot}");
		else
			Console.WriteLine($"[AdminTools] Slot {slot} is not an admin");
	}

	[ConCommand("setdefaultadmin")]
	public void CmdAddAdmin(ConCommandContext ctx) {
		if (!ctx.IsServerCommand) return;
		if (ctx.Args.Length < 2) {
			Console.WriteLine("[AdminTools] Usage: setdefaultadmin <steamid64|slotid|playername>");
			return;
		}

		// Try as SteamID64 first (17-digit number)
		if (ulong.TryParse(ctx.Args[1], out ulong steamId) && steamId > 76561190000000000UL) {
			if (AdminSteamIds.Add(steamId)) {
				SaveConfig();
				// Also grant session admin if they're currently connected
				for (int i = 0; i < 32; i++) {
					var c = Players.FromSlot(i);
					if (c != null && GetSteamId(c) == steamId) {
						_adminSlots.Add(i);
						Console.WriteLine($"[AdminTools] Added persistent admin {steamId} ({c.PlayerName}, now active in slot {i})");
						return;
					}
				}
				Console.WriteLine($"[AdminTools] Added persistent admin {steamId} (not currently connected)");
			} else {
				Console.WriteLine($"[AdminTools] {steamId} is already a persistent admin");
			}
			return;
		}

		// Try as slot or player name — resolve to SteamID
		var controller = FindPlayer(ctx.Args[1]);
		if (controller == null) {
			Console.WriteLine($"[AdminTools] Player not found: {ctx.Args[1]}");
			return;
		}
		ulong sid = GetSteamId(controller);
		if (sid == 0) {
			Console.WriteLine($"[AdminTools] Could not get SteamID for {controller.PlayerName}");
			return;
		}
		int playerSlot = FindSlot(controller);
		if (playerSlot >= 0) _adminSlots.Add(playerSlot);
		if (AdminSteamIds.Add(sid)) {
			SaveConfig();
			Console.WriteLine($"[AdminTools] Added persistent admin {controller.PlayerName} (steam {sid})");
		} else {
			Console.WriteLine($"[AdminTools] {controller.PlayerName} (steam {sid}) is already a persistent admin");
		}
	}

	[ConCommand("removedefaultadmin")]
	public void CmdDelAdmin(ConCommandContext ctx) {
		if (!ctx.IsServerCommand) return;
		if (ctx.Args.Length < 2) {
			Console.WriteLine("[AdminTools] Usage: removedefaultadmin <steamid64|slotid|playername>");
			return;
		}

		// Try as SteamID64 first
		if (ulong.TryParse(ctx.Args[1], out ulong steamId) && steamId > 76561190000000000UL) {
			if (AdminSteamIds.Remove(steamId)) {
				SaveConfig();
				// Also remove session admin if connected
				for (int i = 0; i < 32; i++) {
					var c = Players.FromSlot(i);
					if (c != null && GetSteamId(c) == steamId)
						_adminSlots.Remove(i);
				}
				Console.WriteLine($"[AdminTools] Removed persistent admin {steamId}");
			} else {
				Console.WriteLine($"[AdminTools] {steamId} is not a persistent admin");
			}
			return;
		}

		// Try as slot or player name
		var controller = FindPlayer(ctx.Args[1]);
		if (controller == null) {
			Console.WriteLine($"[AdminTools] Player not found: {ctx.Args[1]}");
			return;
		}
		ulong sid = GetSteamId(controller);
		if (sid == 0) {
			Console.WriteLine($"[AdminTools] Could not get SteamID for {controller.PlayerName}");
			return;
		}
		int playerSlot = FindSlot(controller);
		if (playerSlot >= 0) _adminSlots.Remove(playerSlot);
		if (AdminSteamIds.Remove(sid)) {
			SaveConfig();
			Console.WriteLine($"[AdminTools] Removed persistent admin {controller.PlayerName} (steam {sid})");
		} else {
			Console.WriteLine($"[AdminTools] {controller.PlayerName} (steam {sid}) is not a persistent admin");
		}
	}

	[ConCommand("listadmins")]
	public void CmdListAdmins(ConCommandContext ctx) {
		if (!ctx.IsServerCommand && !IsAuthorized(ctx)) return;

		var lines = new List<string> { "=== AdminTools Admins ===" };

		if (AdminSteamIds.Count > 0) {
			lines.Add("Persistent (saved):");
			foreach (var sid in AdminSteamIds) {
				// Check if currently connected
				string status = "";
				for (int i = 0; i < 32; i++) {
					var c = Players.FromSlot(i);
					if (c != null && GetSteamId(c) == sid) {
						status = $" -> connected as {c.PlayerName} (slot {i})";
						break;
					}
				}
				lines.Add($"  {sid}{status}");
			}
		} else {
			lines.Add("No persistent admins configured.");
		}

		// Show session-only admins (setadmin'd but not in config)
		var sessionOnly = new List<string>();
		foreach (var slot in _adminSlots) {
			var c = Players.FromSlot(slot);
			if (c == null) continue;
			ulong sid = GetSteamId(c);
			if (sid == 0 || !AdminSteamIds.Contains(sid))
				sessionOnly.Add($"  slot {slot}: {c.PlayerName} (steam {sid})");
		}
		if (sessionOnly.Count > 0) {
			lines.Add("Session-only (not saved):");
			lines.AddRange(sessionOnly);
		}

		lines.Add("=========================");
		foreach (var line in lines) {
			if (ctx.IsServerCommand)
				Console.WriteLine(line);
			else
				Log(ctx, line);
		}
	}

	public override HookResult OnClientConCommand(ClientConCommandEvent e) {
		if (!e.Command.Equals("run", StringComparison.OrdinalIgnoreCase))
			return HookResult.Continue;

		var controller = e.Controller;
		if (controller == null) return HookResult.Continue;

		// Find the caller's slot
		int callerSlot = -1;
		for (int i = 0; i < 64; i++) {
			var c = Players.FromSlot(i);
			if (c != null && c.Handle == controller.Handle) {
				callerSlot = i;
				break;
			}
		}

		if (callerSlot < 0 || !_adminSlots.Contains(callerSlot)) {
			Server.ClientCommand(callerSlot, "echo [AdminTools] You are not an admin.");
			return HookResult.Stop;
		}

		if (e.Args.Length < 2) {
			Server.ClientCommand(callerSlot, "echo [AdminTools] Usage: run <command> [args...]");
			return HookResult.Stop;
		}

		string serverCmd = string.Join(" ", e.Args, 1, e.Args.Length - 1);
		string[] serverOnly = ["setadmin", "removeadmin", "setdefaultadmin", "removedefaultadmin", "listadmins"];
		string cmdLower = serverCmd.ToLowerInvariant();
		if (Array.Exists(serverOnly, s => cmdLower.Contains(s))) {
			Server.ClientCommand(callerSlot, "echo [AdminTools] Command blocked: contains a server-only command.");
			return HookResult.Stop;
		}
		Server.ExecuteCommand(serverCmd);
		Server.ClientCommand(callerSlot, $"echo [AdminTools] Executed: {serverCmd}");
		Console.WriteLine($"[AdminTools] Admin slot {callerSlot} executed: {serverCmd}");

		return HookResult.Stop;
	}

	private int FindSlot(CCitadelPlayerController controller) {
		for (int i = 0; i < 32; i++) {
			var c = Players.FromSlot(i);
			if (c != null && c.Handle == controller.Handle) return i;
		}
		return -1;
	}

	private CCitadelPlayerController? FindPlayer(string query) {
		if (int.TryParse(query, out int slot))
			return Players.FromSlot(slot);

		foreach (var controller in Players.GetAll()) {
			if (controller.PlayerName.Contains(query, StringComparison.OrdinalIgnoreCase))
				return controller;
		}
		return null;
	}
}

public class AdminConfig {
	public HashSet<ulong> AdminSteamIds { get; set; } = new();
}

internal class AdminForwardingWriter : TextWriter {
	private readonly TextWriter _original;
	private readonly HashSet<int> _adminSlots;

	public AdminForwardingWriter(TextWriter original, HashSet<int> adminSlots) {
		_original = original;
		_adminSlots = adminSlots;
	}

	public override Encoding Encoding => _original.Encoding;

	public override void Write(char value) => _original.Write(value);
	public override void Write(string? value) => _original.Write(value);

	public override void WriteLine(string? value) {
		_original.WriteLine(value);
		if (value == null || _adminSlots.Count == 0) return;
		foreach (var slot in _adminSlots) {
			if (Players.FromSlot(slot) != null)
				Server.ClientCommand(slot, $"echo {value}");
		}
	}

	public override void WriteLine() {
		_original.WriteLine();
	}
}
