using System.Numerics;

namespace DeadworksManaged.Api;

/// <summary>Base managed wrapper for all Source 2 entities. Provides common operations: health, team, lifecycle, modifiers, schema access.</summary>
public unsafe class CBaseEntity : NativeEntity {
	internal CBaseEntity(nint handle) : base(handle) { }

	/// <summary>Creates a new entity by class name (e.g. "info_particle_system"). Returns null on failure.</summary>
	public static CBaseEntity? CreateByName(string className) {
		Span<byte> utf8 = Utf8.Encode(className, stackalloc byte[Utf8.Size(className)]);
		fixed (byte* ptr = utf8) {
			void* result = NativeInterop.CreateEntityByName(ptr);
			return result != null ? new CBaseEntity((nint)result) : null;
		}
	}

	/// <summary>Gets an entity by its entity handle (CEntityHandle as uint32). Returns null if invalid.</summary>
	public static CBaseEntity? FromHandle(uint handle) {
		if (handle == 0xFFFFFFFF) return null;
		var ptr = (nint)NativeInterop.GetEntityFromHandle(handle);
		return ptr != 0 ? new CBaseEntity(ptr) : null;
	}

	/// <summary>Gets an entity by its global entity index. Returns null if the index is invalid or the entity doesn't exist.</summary>
	public static CBaseEntity? FromIndex(int index) {
		var ptr = (nint)NativeInterop.GetEntityByIndex(index);
		return ptr != 0 ? new CBaseEntity(ptr) : null;
	}

	/// <summary>The designer/map name (e.g. "npc_boss_tier3", "player").</summary>
	public string DesignerName {
		get {
			byte* ptr = NativeInterop.GetEntityDesignerName((void*)Handle);
			return System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)ptr) ?? "";
		}
	}

	private static readonly SchemaAccessor<nint> _pEntity = new("CEntityInstance"u8, "m_pEntity"u8);
	private static readonly SchemaAccessor<nint> _entityName = new("CEntityIdentity"u8, "m_name"u8);

	/// <summary>The entity name (targetname set in Hammer or via code).</summary>
	public string Name {
		get {
			nint identity = _pEntity.Get(Handle);
			if (identity == 0) return "";
			nint namePtr = _entityName.Get(identity);
			return namePtr != 0 ? System.Runtime.InteropServices.Marshal.PtrToStringUTF8(namePtr) ?? "" : "";
		}
	}

	/// <summary>The C++ DLL class name (e.g. "CCitadelPlayerPawn", "CBaseEntity").</summary>
	public string Classname {
		get {
			byte* ptr = NativeInterop.GetEntityClassname((void*)Handle);
			return System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)ptr) ?? "";
		}
	}

	/// <summary>Check if this entity's native type matches T's class name.</summary>
	public bool Is<T>() where T : CBaseEntity {
		return NativeEntityFactory.IsMatch<T>(Classname);
	}

	/// <summary>Cast this entity to T if the native type matches, otherwise null.</summary>
	public T? As<T>() where T : CBaseEntity {
		return Is<T>() ? NativeEntityFactory.Create<T>(Handle) : null;
	}

	/// <summary>Marks this entity for removal at the end of the current frame (UTIL_Remove).</summary>
	public void Remove() => NativeInterop.RemoveEntity((void*)Handle);

	/// <summary>Gets the entity handle (CEntityHandle as uint32) for this entity.</summary>
	public uint EntityHandle => NativeInterop.GetEntityHandle((void*)Handle);

	/// <summary>Gets the entity index (lower 14 bits of the handle).</summary>
	public int EntityIndex => (int)(EntityHandle & 0x3FFF);

	/// <summary>Queues and executes entity spawn.</summary>
	public void Spawn() {
		NativeInterop.QueueSpawnEntity((void*)Handle, null);
		NativeInterop.ExecuteQueuedCreation();
	}

	/// <summary>Queues and executes entity spawn with CEntityKeyValues.</summary>
	public void Spawn(void* ekv) {
		NativeInterop.QueueSpawnEntity((void*)Handle, ekv);
		NativeInterop.ExecuteQueuedCreation();
	}

	/// <summary>Queues and executes entity spawn with CEntityKeyValues.</summary>
	public void Spawn(CEntityKeyValues ekv) {
		NativeInterop.QueueSpawnEntity((void*)Handle, ekv.Handle);
		NativeInterop.ExecuteQueuedCreation();
	}

	/// <summary>Teleports this entity. Pass null for any parameter to leave it unchanged.</summary>
	public void Teleport(Vector3? position = null, Vector3? angles = null, Vector3? velocity = null) {
		Vector3 pos = position.GetValueOrDefault();
		Vector3 ang = angles.GetValueOrDefault();
		Vector3 vel = velocity.GetValueOrDefault();
		NativeInterop.Teleport((void*)Handle,
			position.HasValue ? (float*)&pos : null,
			angles.HasValue ? (float*)&ang : null,
			velocity.HasValue ? (float*)&vel : null);
	}

	/// <summary>Fires an entity input (e.g. "Start", "Stop", "SetParent").</summary>
	public void AcceptInput(string inputName, CBaseEntity? activator = null, CBaseEntity? caller = null, string? value = null) {
		Span<byte> utf8Input = Utf8.Encode(inputName, stackalloc byte[Utf8.Size(inputName)]);

		int valLen = value != null ? Utf8.Size(value) : 1;
		Span<byte> utf8Val = stackalloc byte[valLen];
		utf8Val[0] = 0;
		if (value != null)
			Utf8.Encode(value, utf8Val);

		fixed (byte* inputPtr = utf8Input)
		fixed (byte* vPtr = utf8Val) {
			NativeInterop.AcceptInput((void*)Handle, inputPtr,
				activator != null ? (void*)activator.Handle : null,
				caller != null ? (void*)caller.Handle : null,
				value != null ? vPtr : null);
		}
	}

	/// <summary>Sets this entity's parent via AcceptInput("SetParent", activator: parent, value: "!activator").</summary>
	public void SetParent(CBaseEntity parent) => AcceptInput("SetParent", activator: parent, value: "!activator");

	/// <summary>Clears this entity's parent.</summary>
	public void ClearParent() => AcceptInput("ClearParent");

	/// <summary>Adds a modifier by VData name (e.g. "modifier_ui_hud_message").</summary>
	public CBaseModifier? AddModifier(string name, KeyValues3? kv = null, CBaseEntity? caster = null, CBaseEntity? ability = null, int team = 0) {
		Span<byte> utf8Name = Utf8.Encode(name, stackalloc byte[Utf8.Size(name)]);

		fixed (byte* namePtr = utf8Name) {
			var result = NativeInterop.AddModifier(
				(void*)Handle, namePtr,
				kv != null ? (void*)kv.Handle : null,
				caster != null ? (void*)caster.Handle : null,
				ability != null ? (void*)ability.Handle : null,
				team, null, null, 0);
			return result != null ? new CBaseModifier((nint)result) : null;
		}
	}

	/// <summary>
	/// Adds a modifier with per-instance ability property value overrides.
	/// Use this to apply modifiers without a real ability, or to override the default
	/// values from the ability's property map. Property names match the VData's
	/// m_vecAutoRegisterModifierValueFromAbilityPropertyName entries.
	/// </summary>
	/// <example>
	/// pawn.AddModifier("ability_doorman_bomb/debuff", kv: kv,
	///     abilityValues: new() { ["SlowPercent"] = 100.0f });
	/// </example>
	public CBaseModifier? AddModifier(string name, Dictionary<string, float> abilityValues,
		KeyValues3? kv = null, CBaseEntity? caster = null, CBaseEntity? ability = null, int team = 0) {

		if (abilityValues.Count == 0)
			return AddModifier(name, kv, caster, ability, team);

		Span<byte> utf8Name = Utf8.Encode(name, stackalloc byte[Utf8.Size(name)]);

		int count = abilityValues.Count;
		Span<float> values = count <= 16 ? stackalloc float[count] : new float[count];

		// Encode all property names as null-terminated UTF-8 into a contiguous buffer
		int totalBytes = 0;
		foreach (var kvp in abilityValues)
			totalBytes += Utf8.Size(kvp.Key);
		Span<byte> nameBuf = totalBytes <= 1024 ? stackalloc byte[totalBytes] : new byte[totalBytes];

		int offset = 0;
		int idx = 0;
		foreach (var kvp in abilityValues) {
			int len = Utf8.Size(kvp.Key);
			Utf8.Encode(kvp.Key, nameBuf.Slice(offset, len));
			values[idx] = kvp.Value;
			offset += len;
			idx++;
		}

		fixed (byte* namePtr = utf8Name)
		fixed (float* valPtr = values)
		fixed (byte* nameBufPtr = nameBuf) {
			byte** namePtrs = stackalloc byte*[count];
			int off = 0;
			idx = 0;
			foreach (var kvp in abilityValues) {
				namePtrs[idx] = nameBufPtr + off;
				off += Utf8.Size(kvp.Key);
				idx++;
			}

			var result = NativeInterop.AddModifier(
				(void*)Handle, namePtr,
				kv != null ? (void*)kv.Handle : null,
				caster != null ? (void*)caster.Handle : null,
				ability != null ? (void*)ability.Handle : null,
				team, namePtrs, valPtr, count);
			return result != null ? new CBaseModifier((nint)result) : null;
		}
	}

	/// <summary>Plays a sound event on this entity.</summary>
	public void EmitSound(string soundName, int pitch = 100, float volume = 1.0f, float delay = 0.0f) {
		Span<byte> utf8 = Utf8.Encode(soundName, stackalloc byte[Utf8.Size(soundName)]);
		fixed (byte* ptr = utf8) {
			NativeInterop.EmitSound((void*)Handle, ptr, pitch, volume, delay);
		}
	}

	private static readonly SchemaAccessor<nint> _bodyComponent = new("CBaseEntity"u8, "m_CBodyComponent"u8);
	public CBodyComponent? BodyComponent {
		get {
			nint ptr = _bodyComponent.Get(Handle);
			return ptr != 0 ? new CBodyComponent(ptr) : null;
		}
	}

	public Vector3 Position => BodyComponent?.SceneNode?.AbsOrigin ?? Vector3.Zero;

	private static readonly SchemaAccessor<int> _health = new("CBaseEntity"u8, "m_iHealth"u8);
	public int Health { get => _health.Get(Handle); set => _health.Set(Handle, value); }

	private static readonly SchemaAccessor<int> _maxHealth = new("CBaseEntity"u8, "m_iMaxHealth"u8);
	public int MaxHealth { get => _maxHealth.Get(Handle); set => _maxHealth.Set(Handle, value); }

	private static readonly SchemaAccessor<byte> _teamNum = new("CBaseEntity"u8, "m_iTeamNum"u8);
	public int TeamNum { get => _teamNum.Get(Handle); set => _teamNum.Set(Handle, (byte)value); }

	private static readonly SchemaAccessor<uint> _lifeState = new("CBaseEntity"u8, "m_lifeState"u8);
	public LifeState LifeState { get => (LifeState)_lifeState.Get(Handle); set => _lifeState.Set(Handle, (uint)value); }
	public bool IsAlive => LifeState == LifeState.Alive;

	private static readonly SchemaAccessor<nint> _modifierProp = new("CBaseEntity"u8, "m_pModifierProp"u8);
	public CModifierProperty? ModifierProp {
		get {
			nint ptr = _modifierProp.Get(Handle);
			return ptr != 0 ? new CModifierProperty(ptr) : null;
		}
	}

	// m_pSubclassVData is at m_nSubclassID + 4 (CUtlStringToken is 4 bytes)
	private static readonly SchemaAccessor<byte> _subclassID = new("CBaseEntity"u8, "m_nSubclassID"u8);
	public CEntitySubclassVDataBase? SubclassVData {
		get {
			nint pVData = *(nint*)((byte*)_subclassID.GetAddress(Handle) + 4);
			return pVData != 0 ? new CEntitySubclassVDataBase(pVData) : null;
		}
	}

	/// <summary>Applies damage to this entity using UTIL_InflictGenericDamage (convenience wrapper around <see cref="TakeDamage"/>).</summary>
	public void Hurt(float damage, CBaseEntity? attacker = null, CBaseEntity? inflictor = null, CBaseEntity? ability = null, int damageType = 0) {
		NativeInterop.HurtEntity(
			(void*)Handle,
			attacker != null ? (void*)attacker.Handle : null,
			inflictor != null ? (void*)inflictor.Handle : null,
			ability != null ? (void*)ability.Handle : null,
			damage, damageType);
	}

	/// <summary>Applies damage to this entity using an existing <see cref="CTakeDamageInfo"/> struct.</summary>
	public void TakeDamage(CTakeDamageInfo info) {
		NativeInterop.TakeDamage((void*)Handle, (void*)info.Handle);
	}

	/// <summary>Sets the model for this entity (e.g. "models/heroes_wip/werewolf/werewolf.vmdl").</summary>
	public void SetModel(string modelName) {
		Span<byte> utf8 = Utf8.Encode(modelName, stackalloc byte[Utf8.Size(modelName)]);
		fixed (byte* ptr = utf8) {
			NativeInterop.SetModel((void*)Handle, ptr);
		}
	}

	/// <summary>Read any schema field by class and field name. For repeated access, prefer a static <see cref="SchemaAccessor{T}"/> instead.</summary>
	public T GetField<T>(ReadOnlySpan<byte> className, ReadOnlySpan<byte> fieldName) where T : unmanaged
		=> new SchemaAccessor<T>(className, fieldName).Get(Handle);

	/// <summary>Write any schema field by class and field name. For repeated access, prefer a static <see cref="SchemaAccessor{T}"/> instead.</summary>
	public void SetField<T>(ReadOnlySpan<byte> className, ReadOnlySpan<byte> fieldName, T value) where T : unmanaged
		=> new SchemaAccessor<T>(className, fieldName).Set(Handle, value);
}
