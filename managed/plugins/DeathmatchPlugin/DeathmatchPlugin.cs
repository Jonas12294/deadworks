using System.Numerics;
using System.Text.Json.Serialization;
using DeadworksManaged.Api;

namespace DeathmatchPlugin;

public class SpawnPoint {
	[JsonPropertyName("pos")]
	public float[] Pos { get; set; } = [0, 0, 0];

	[JsonPropertyName("ang")]
	public float[] Ang { get; set; } = [0, 0, 0];
}

public class DeathmatchConfig {
	public Dictionary<string, Dictionary<string, SpawnPoint[]>> SpawnPoints { get; set; } = new();
}

public class DeathmatchPlugin : DeadworksPluginBase {
	public override string Name => "Deathmatch";

	[PluginConfig]
	public DeathmatchConfig Config { get; set; } = new();

	private readonly Dictionary<int, string> _playerNames = new();
	private bool _rebroadcasting;

	public override void OnLoad(bool isReload) {
		Console.WriteLine(isReload ? "Deathmatch reloaded!" : "Deathmatch loaded!");
	}

	public override void OnClientPutInServer(ClientPutInServerEvent args) {
		_playerNames[args.Slot] = args.Name;
	}

	[ChatCommand("pos")]
	public HookResult CmdPos(ChatCommandContext ctx) {
		var pawn = ctx.Controller?.GetHeroPawn()?.As<CCitadelPlayerPawn>();
		if (pawn == null) return HookResult.Handled;
		var pos = pawn.Position;
		var ang = pawn.CameraAngles;
		Console.WriteLine($@"{{ ""pos"": [{pos.X}, {pos.Y}, {pos.Z}], ""ang"": [{ang.X}, {ang.Y}, {ang.Z}] }}");
		return HookResult.Handled;
	}

	private EntityData<(float, float, float)> _lastMoves = new();

	private int _count = 0;
	public override void OnProcessUsercmds(ProcessUsercmdsEvent args) {
		var controller = args.Controller;
		if (controller == null) return;
		foreach (var cmd in args.Usercmds) {
		}
	}

	private int forcingState = 0;
	private int counter = 0;

	public override void OnAbilityAttempt(AbilityAttemptEvent args) {
		if (args.ChangedButtons != 0) {
			Console.WriteLine($"ChangedButtons: {args.ChangedButtons}");
		}

		counter++;
		if (forcingState == 1) {
			args.Force(InputButton.Ability4);
			args.Force(InputButton.AltCast);
			forcingState = 2;
			counter = 0;
		} else if (forcingState == 2 && counter > 5) {
			// args.Force(InputButton.AltCast);
			forcingState = 0;
		}
	}

	[ChatCommand("paa")]
	public HookResult CmdPaa(ChatCommandContext ctx) {
		forcingState = 1;
		if (false) {
			var comp = ctx.Controller?.GetHeroPawn()?.AbilityComponent;
			if (comp != null) {
				var ability = comp.GetAbilityBySlot(EAbilitySlot.Signature3);
				if (ability != null) {
					comp.ToggleActivate(ability, false);
					comp.ExecuteAbility(ability);
					// comp.ToggleActivate(ability, false);
				}
			}
		}
		return HookResult.Handled;
	}

	[ChatCommand("name")]
	public HookResult CmdName(ChatCommandContext ctx) {
		Console.WriteLine($"{ctx.Controller?.PlayerName}");
		return HookResult.Handled;
	}

	[ChatCommand("die")]
	public HookResult CmdDie(ChatCommandContext ctx) {
		var pawn = ctx.Controller?.GetHeroPawn()?.As<CCitadelPlayerPawn>();
		var dmgInfo = new CTakeDamageInfo(10000f, attacker: pawn);
		dmgInfo.DamageFlags |= TakeDamageFlags.AllowSuicide;
		pawn?.TakeDamage(dmgInfo);
		return HookResult.Handled;
	}

	[ChatCommand("bleh")]
	public HookResult CmdBleh(ChatCommandContext ctx) {
		var pawn = ctx.Controller?.GetHeroPawn()?.As<CCitadelPlayerPawn>();
		if (pawn == null) return HookResult.Handled;

		using var kv = new KeyValues3();
		kv.SetFloat("duration", 10.0f);
		/*pawn.AddModifier("citadel_ability_tangotether/citadel_modifier_tangotether_tether/citadel_modifier_tangotether_tether_receiver",
			abilityValues: new() {
				["BonusFireRate"] = 1000,
			}, kv: kv);*/

		var ability = pawn.AbilityComponent.GetAbilityBySlot(EAbilitySlot.Innate1);
		pawn.AddModifier("synth_affliction/modifier_synth_affliction_debuff",
			abilityValues: new() { ["DPS"] = 5.0f },
			kv: kv, ability: ability);

		// var ability = pawn.AbilityComponent.GetAbilityBySlot(EAbilitySlot.Innate1);
		// pawn.AddModifier("modifier_citadel_knockdown", kv: kv);

		return HookResult.Handled;
	}

	[GameEventHandler("player_respawned")]
	public HookResult OnPlayerRespawned(PlayerRespawnedEvent args) {
		var pawn = args.Userid;
		if (pawn == null) return HookResult.Continue;

		var teamKey = pawn.TeamNum.ToString();
		if (Config.SpawnPoints.TryGetValue(Server.MapName, out var teams)
			&& teams.TryGetValue(teamKey, out var spawns)
			&& spawns.Length > 0) {
			var spawn = spawns[Random.Shared.Next(spawns.Length)];
			var pos = spawn.Pos.Length >= 3 ? new Vector3(spawn.Pos[0], spawn.Pos[1], spawn.Pos[2]) : (Vector3?)null;
			var ang = spawn.Ang.Length >= 3 ? new Vector3(spawn.Ang[0], spawn.Ang[1], spawn.Ang[2]) : (Vector3?)null;
			pawn.Teleport(position: pos, angles: ang);
			pawn.Health = pawn.MaxHealth;
		}

		return HookResult.Continue;
	}

	public override HookResult OnClientConCommand(ClientConCommandEvent e) {
		Console.WriteLine($"[ConCmd] {e.Command} (args: {string.Join(", ", e.Args)})");
		if (e.Command == "selecthero") {
			var pawn = e.Controller?.GetHeroPawn()?.As<CCitadelPlayerPawn>();
			// Health is a silly heuristic
			if (pawn != null && !pawn.InRegenerationZone && pawn.Health > 0) {
				var controller = e.Controller;
				if (controller != null) {
					int slot = controller.EntityIndex - 1;
					var msg = new CCitadelUserMsg_ChatMsg {
						PlayerSlot = slot,
						Text = "[server] You can only change heroes while in spawn",
						AllChat = true,
					};
					NetMessages.Send(msg, RecipientFilter.Single(slot));
				}
				return HookResult.Stop;
			}
		}
		return HookResult.Continue;
	}

	/// <summary>
	/// Rebroadcasts chat messages so each recipient sees the message as coming from
	/// their own player slot (guaranteed to have a portrait), with the actual sender's
	/// name prefixed in the text. This works around Deadlock's 12-slot portrait limit.
	/// </summary>
	[NetMessageHandler]
	public HookResult OnChatMsgOutgoing(OutgoingMessageContext<CCitadelUserMsg_ChatMsg> ctx) {
		// Reentrancy guard — our own Send calls trigger this hook again
		if (_rebroadcasting) return HookResult.Continue;

		var senderSlot = ctx.Message.PlayerSlot;

		// Let system/server messages pass through unchanged
		if (senderSlot < 0) return HookResult.Continue;

		var text = ctx.Message.Text;
		var allChat = ctx.Message.AllChat;
		var laneColor = ctx.Message.LaneColor;
		var originalMask = ctx.Recipients.Mask;

		var senderName = _playerNames.GetValueOrDefault(senderSlot, $"Player {senderSlot}");

		// Rebroadcast to each recipient individually with their own slot
		_rebroadcasting = true;
		try {
			for (int slot = 0; slot < 64; slot++) {
				if ((originalMask & (1UL << slot)) == 0) continue;

				var msg = new CCitadelUserMsg_ChatMsg {
					PlayerSlot = slot,
					Text = slot == senderSlot ? text : $"[{senderName}]: {text}",
					AllChat = allChat,
					LaneColor = laneColor
				};
				NetMessages.Send(msg, RecipientFilter.Single(slot));
			}
		} finally {
			_rebroadcasting = false;
		}

		// Suppress the original broadcast
		return HookResult.Stop;
	}

	[ChatCommand("test1")]
	public HookResult CmdTest1(ChatCommandContext ctx) {
		var pawn = ctx.Controller?.GetHeroPawn();
		if (pawn == null) {
			Console.WriteLine("No pawn found");
			return HookResult.Handled;
		}
		Console.WriteLine($"m_bInRegenerationZone = {pawn.InRegenerationZone}");
		return HookResult.Handled;
	}

	[ChatCommand("trace")]
	public HookResult CmdTrace(ChatCommandContext ctx) {
		var pawn = ctx.Controller?.GetHeroPawn()?.As<CCitadelPlayerPawn>();
		if (pawn == null) {
			Console.WriteLine("No pawn found for trace");
			return HookResult.Handled;
		}

		var eye = pawn.EyePosition;
		var eyeAngles = pawn.EyeAngles;
		var camAngles = pawn.CameraAngles;
		var viewAngles = pawn.ViewAngles;

		Console.WriteLine($"[trace] EyeAngles=({eyeAngles.X:F4},{eyeAngles.Y:F4},{eyeAngles.Z:F4}) [networked, quantized 11-bit]");
		Console.WriteLine($"[trace] CamAngles=({camAngles.X:F4},{camAngles.Y:F4},{camAngles.Z:F4}) [m_angClientCamera]");
		Console.WriteLine($"[trace] ViewAngles=({viewAngles.X:F4},{viewAngles.Y:F4},{viewAngles.Z:F4}) [v_angle, raw from CUserCmd]");
		Console.WriteLine($"[trace] EyePos=({eye.X:F1},{eye.Y:F1},{eye.Z:F1}) AbsOrigin=({pawn.Position.X:F1},{pawn.Position.Y:F1},{pawn.Position.Z:F1})");

		// Use v_angle — raw server-side view angles from user commands, no quantization
		var angles = viewAngles;
		float pitch = angles.X * MathF.PI / 180f;
		float yaw = angles.Y * MathF.PI / 180f;
		var forward = new System.Numerics.Vector3(
			MathF.Cos(pitch) * MathF.Cos(yaw),
			MathF.Cos(pitch) * MathF.Sin(yaw),
			-MathF.Sin(pitch));

		var end = eye + forward * 10000f;

		Console.WriteLine($"[trace] eye=({eye.X:F1},{eye.Y:F1},{eye.Z:F1}) end=({end.X:F1},{end.Y:F1},{end.Z:F1}) pawnIdx={pawn.EntityIndex}");

		// Direct trace with minimal setup for debugging
		unsafe {
			var trace = CGameTrace.Create();
			var ray = new Ray_t { Type = RayType_t.Line };
			var filter = new CTraceFilter(true) {
				IterateEntities = true, // multi-hit path, calls ShouldHitEntity to filter player
				QueryShapeAttributes = new RnQueryShapeAttr_t {
					ObjectSetMask = RnQueryObjectSet.All,
					InteractsWith = MaskTrace.Solid,
					InteractsExclude = MaskTrace.Empty,
					InteractsAs = MaskTrace.Empty,
					CollisionGroup = CollisionGroup.CitadelBullet,
					HitSolid = true,
				}
			};
			filter.QueryShapeAttributes.EntityIdsToIgnore[0] = (uint)pawn.EntityIndex;

			Console.WriteLine($"[trace] sizeof Ray_t={sizeof(Ray_t)} CTraceFilter={sizeof(CTraceFilter)} CGameTrace={sizeof(CGameTrace)}");
			Console.WriteLine($"[trace] filter EntityIdsToIgnore[0]={filter.QueryShapeAttributes.EntityIdsToIgnore[0]}");

			Trace.TraceShape(eye, end, ray, filter, ref trace);

			Console.WriteLine($"[trace] frac={trace.Fraction:F6} startInSolid={trace.StartInSolid} pEntity=0x{trace.pEntity:X}");
			Console.WriteLine($"[trace] hitPoint=({trace.HitPoint.X:F1},{trace.HitPoint.Y:F1},{trace.HitPoint.Z:F1})");
			Console.WriteLine($"[trace] startPos=({trace.StartPos.X:F1},{trace.StartPos.Y:F1},{trace.StartPos.Z:F1})");
			Console.WriteLine($"[trace] endPos=({trace.EndPos.X:F1},{trace.EndPos.Y:F1},{trace.EndPos.Z:F1})");

			var slot = ctx.Message.SenderSlot;
			var hitPos = eye + (end - eye) * trace.Fraction;
			var text = trace.DidHit
				? $"Trace hit at ({hitPos.X:F1}, {hitPos.Y:F1}, {hitPos.Z:F1}) frac={trace.Fraction:F4}"
				: "Trace: no hit";

			Console.WriteLine(text);
			var msg = new CCitadelUserMsg_ChatMsg {
				PlayerSlot = slot,
				Text = text,
				AllChat = true
			};
			NetMessages.Send(msg, RecipientFilter.Single(slot));

			return HookResult.Handled;
		}
	}

	public override void OnUnload() => Console.WriteLine("Deathmatch unloaded!");

	public override void OnPrecacheResources() {
	}

	public override void OnStartupServer() {
		ConVar.Find("citadel_active_lane")?.SetInt(4);
		ConVar.Find("citadel_player_spawn_time_max_respawn_time")?.SetInt(5);
		ConVar.Find("citadel_allow_purchasing_anywhere")?.SetInt(1);
		ConVar.Find("citadel_item_sell_price_ratio")?.SetFloat(1.0f);
		ConVar.Find("citadel_voice_all_talk")?.SetInt(1);
		ConVar.Find("citadel_player_starting_gold")?.SetInt(0);
		ConVar.Find("citadel_trooper_spawn_enabled")?.SetInt(0);
		ConVar.Find("citadel_npc_spawn_enabled")?.SetInt(0);
		ConVar.Find("citadel_start_players_on_zipline")?.SetInt(0);
		ConVar.Find("citadel_allow_duplicate_heroes")?.SetInt(1);
	}

	public override HookResult OnTakeDamage(TakeDamageEvent args) {
		if (args.Entity.DesignerName == "npc_boss_tier3" || args.Entity.DesignerName == "npc_boss_tier2" || args.Entity.DesignerName == "npc_trooper_boss")
			return HookResult.Stop;
		return HookResult.Continue;
	}

	public override HookResult OnModifyCurrency(ModifyCurrencyEvent args) {
		if (args.CurrencyType == ECurrencyType.EGold) {
			if (args.Source == ECurrencySource.EStartingAmount) {
				// Trigger boons by reissuing as non-starting amount
				args.Pawn.ModifyCurrency(ECurrencyType.EGold, 15_000, ECurrencySource.ECheats);
				args.Pawn.ModifyCurrency(ECurrencyType.EAbilityPoints, 17, ECurrencySource.ECheats);
				return HookResult.Stop;
			}
			if (args.Source != ECurrencySource.ECheats && args.Source != ECurrencySource.EItemPurchase && args.Source != ECurrencySource.EItemSale)
				return HookResult.Stop;
		}
		return HookResult.Continue;
	}

	[GameEventHandler("player_hero_changed")]
	public HookResult OnPlayerHeroChanged(PlayerHeroChangedEvent args) {
		// Otherwise AP carries
		args.Userid?.As<CCitadelPlayerPawn>()?.ResetHero();
		return HookResult.Continue;
	}

	public override void OnEntitySpawned(EntitySpawnedEvent e) {
		var designerNamesToRemove = new HashSet<string>() { "npc_trooper_boss" };
		var namesToRemove = new HashSet<string>() {
		};
		if (designerNamesToRemove.Contains(e.Entity.DesignerName) || namesToRemove.Contains(e.Entity.Name)) {
			e.Entity.Remove();
		}
	}

	public override void OnClientFullConnect(ClientFullConnectEvent args) {
		var controller = args.Controller;
		if (controller == null) return;

		int team2 = 0, team3 = 0;
		for (int i = 0; i < 64; i++) {
			var ent = CBaseEntity.FromIndex(i + 1);
			if (ent == null) continue;
			if (ent.TeamNum == 2) team2++;
			else if (ent.TeamNum == 3) team3++;
		}
		int team = team2 < team3 ? 2 : team3 < team2 ? 3 : Random.Shared.Next(2) == 0 ? 2 : 3;
		Console.WriteLine($"Assigning {args.Slot} to team {team}");
		controller.ChangeTeam(team);

		var heroes = Enum.GetValues<Heroes>()
			.Where(h => h.GetHeroData()?.AvailableInGame == true)
			.ToArray();
		var hero = heroes[Random.Shared.Next(heroes.Length)];

		Console.WriteLine($"Assigning {args.Slot} to hero {hero.ToHeroName()}");
		controller.SelectHero(hero);
	}

	public override void OnClientDisconnect(ClientDisconnectedEvent args) {
		_playerNames.Remove(args.Slot);

		var controller = args.Controller;
		if (controller == null) return;

		var pawn = controller.GetHeroPawn();
		if (pawn == null) return;

		pawn.Remove();
	}
}
