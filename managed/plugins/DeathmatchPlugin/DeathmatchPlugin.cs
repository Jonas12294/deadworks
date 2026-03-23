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
	public int HeroSwapIntervalSeconds { get; set; } = 300;
}

public class DeathmatchPlugin : DeadworksPluginBase {
	public override string Name => "Deathmatch";

	[PluginConfig]
	public DeathmatchConfig Config { get; set; } = new();

	private Heroes[] _availableHeroes = [];
	private Heroes _team2Hero;
	private Heroes _team3Hero;
	private IHandle? _swapTimer;

	public override void OnLoad(bool isReload) {
		Console.WriteLine(isReload ? "Deathmatch reloaded!" : "Deathmatch loaded!");
	}

	public override void OnConfigReloaded() => RestartSwapTimer();

	private void RestartSwapTimer() {
		_swapTimer?.Cancel();
		var interval = Config.HeroSwapIntervalSeconds;
		if (interval > 0) {
			_swapTimer = Timer.Every(interval.Seconds(), SwapHeroes);
			Console.WriteLine($"[DM] Hero swap every {interval}s");
		} else {
			Console.WriteLine("[DM] Hero swap disabled");
		}
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

		Timer.NextTick(() => {
			var (hero1, hero2) = PickTwoRandomHeroes();
			_team2Hero = hero1;
			_team3Hero = hero2;
			Console.WriteLine($"[DM] Team 2: {_team2Hero.ToHeroName()}, Team 3: {_team3Hero.ToHeroName()}");
		});

		RestartSwapTimer();

		Timer.Once(3.Seconds(), () => {
			var sign1 = CPointWorldText.Create("DEADWORKS.net", new Vector3(0, 256, 542), fontSize: 100f, r: 127, g: 0, b: 127, fontName: "Reaver");
			sign1?.Teleport(angles: new Vector3(185f, 0f, 270f));
			sign1?.WorldUnitsPerPx = 0.50f;
			sign1?.JustifyHorizontal = HorizontalJustify.Center;
			sign1?.JustifyVertical = VerticalJustify.Center;
			var sign2 = CPointWorldText.Create("DEADWORKS.net", new Vector3(0, -256, 542), fontSize: 100f, r: 127, g: 0, b: 127, fontName: "Reaver");
			sign2?.Teleport(angles: new Vector3(185f, 180f, 270f));
			sign2?.WorldUnitsPerPx = 0.50f;
			sign2?.JustifyHorizontal = HorizontalJustify.Center;
			sign2?.JustifyVertical = VerticalJustify.Center;
		});
	}

	private (Heroes, Heroes) PickTwoRandomHeroes() {
		_availableHeroes = Enum.GetValues<Heroes>()
			.Where(h => h.GetHeroData()?.AvailableInGame == true)
			.ToArray();

		var first = _availableHeroes[Random.Shared.Next(_availableHeroes.Length)];
		Heroes second;
		do {
			second = _availableHeroes[Random.Shared.Next(_availableHeroes.Length)];
		} while (second == first);
		return (first, second);
	}

	private void SwapHeroes() {
		(_team2Hero, _team3Hero) = PickTwoRandomHeroes();
		Console.WriteLine($"[DM] New heroes! Team 2: {_team2Hero.ToHeroName()}, Team 3: {_team3Hero.ToHeroName()}");

		Chat.PrintToChatAll("[DM] New heroes!");

		foreach (var controller in Players.GetAll()) {
			var pawn = controller.GetHeroPawn();
			if (pawn == null) continue;
			controller.SelectHero(pawn.TeamNum == 2 ? _team2Hero : _team3Hero);
		}
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
		}

		MaxUpgradeSignatureAbilities(pawn.As<CCitadelPlayerPawn>());
		return HookResult.Continue;
	}

	public override HookResult OnClientConCommand(ClientConCommandEvent e) {
		if (e.Command == "selecthero") {
			return HookResult.Stop;
		}
		if (e.Command == "changeteam" || e.Command == "jointeam") {
			return HookResult.Stop;
		}
		return HookResult.Continue;
	}

	[GameEventHandler("player_hero_changed")]
	public HookResult OnPlayerHeroChanged(PlayerHeroChangedEvent args) {
		var pawn = args.Userid?.As<CCitadelPlayerPawn>();
		if (pawn != null) {
			pawn.ResetHero();
			pawn.Heal(pawn.GetMaxHealth());
			MaxUpgradeSignatureAbilities(pawn);
		}
		return HookResult.Continue;
	}

	public override void OnEntitySpawned(EntitySpawnedEvent e) {
		if (e.Entity.DesignerName == "npc_trooper_boss")
			e.Entity.Remove();
	}

	public override void OnClientFullConnect(ClientFullConnectEvent args) {
		var controller = args.Controller;
		if (controller == null) return;

		int team2 = 0, team3 = 0;
		foreach (var p in Players.GetAll()) {
			if (p.EntityIndex == controller.EntityIndex) continue;
			var pawn = p.GetHeroPawn();
			if (pawn == null) continue;
			if (pawn.TeamNum == 2) team2++;
			else if (pawn.TeamNum == 3) team3++;
		}
		int team = team2 < team3 ? 2 : team3 < team2 ? 3 : Random.Shared.Next(2) == 0 ? 2 : 3;
		controller.ChangeTeam(team);

		var hero = team == 2 ? _team2Hero : _team3Hero;
		Console.WriteLine($"[DM] Slot {args.Slot} -> team {team}, hero {hero.ToHeroName()}");
		controller.SelectHero(hero);
	}

	public override void OnClientDisconnect(ClientDisconnectedEvent args) {
		args.Controller?.GetHeroPawn()?.Remove();
	}

	public override HookResult OnTakeDamage(TakeDamageEvent args) {
		if (args.Entity.DesignerName is "npc_boss_tier3" or "npc_boss_tier2" or "npc_trooper_boss")
			return HookResult.Stop;
		return HookResult.Continue;
	}

	public override HookResult OnModifyCurrency(ModifyCurrencyEvent args) {
		if (args.CurrencyType == ECurrencyType.EGold) {
			if (args.Source == ECurrencySource.EStartingAmount) {
				args.Pawn.ModifyCurrency(ECurrencyType.EGold, 15_000, ECurrencySource.ECheats);
				args.Pawn.ModifyCurrency(ECurrencyType.EAbilityPoints, 17, ECurrencySource.ECheats);
				return HookResult.Stop;
			}
			if (args.Source != ECurrencySource.ECheats && args.Source != ECurrencySource.EItemPurchase && args.Source != ECurrencySource.EItemSale)
				return HookResult.Stop;
		}
		return HookResult.Continue;
	}

	private bool _rebroadcasting;

	[NetMessageHandler]
	public HookResult OnChatMsgOutgoing(OutgoingMessageContext<CCitadelUserMsg_ChatMsg> ctx) {
		if (_rebroadcasting) return HookResult.Continue;

		var senderSlot = ctx.Message.PlayerSlot;
		if (senderSlot < 0) return HookResult.Continue;

		var text = ctx.Message.Text;
		var allChat = ctx.Message.AllChat;
		var laneColor = ctx.Message.LaneColor;
		var originalMask = ctx.Recipients.Mask;
		var senderName = CBaseEntity.FromIndex<CCitadelPlayerController>(senderSlot)?.PlayerName ?? $"Player {senderSlot}";

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

		return HookResult.Stop;
	}

	public override void OnUnload() {
		_swapTimer?.Cancel();
		Console.WriteLine("Deathmatch unloaded!");
	}

	public override void OnPrecacheResources() {
	}

	private static void MaxUpgradeSignatureAbilities(CCitadelPlayerPawn? pawn) {
		if (pawn == null) return;
		foreach (var ability in pawn.AbilityComponent.Abilities) {
			if (ability.AbilitySlot > EAbilitySlot.Signature4) continue;
			ability.UpgradeBits = ability.UpgradeBits | 0b11111;
		}
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

		var angles = viewAngles;
		float pitch = angles.X * MathF.PI / 180f;
		float yaw = angles.Y * MathF.PI / 180f;
		var forward = new System.Numerics.Vector3(
			MathF.Cos(pitch) * MathF.Cos(yaw),
			MathF.Cos(pitch) * MathF.Sin(yaw),
			-MathF.Sin(pitch));

		var end = eye + forward * 10000f;

		Console.WriteLine($"[trace] eye=({eye.X:F1},{eye.Y:F1},{eye.Z:F1}) end=({end.X:F1},{end.Y:F1},{end.Z:F1}) pawnIdx={pawn.EntityIndex}");

		unsafe {
			var trace = CGameTrace.Create();
			var ray = new Ray_t { Type = RayType_t.Line };
			var filter = new CTraceFilter(true) {
				IterateEntities = true,
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
			Chat.PrintToChat(slot, text);

			return HookResult.Handled;
		}
	}
}
