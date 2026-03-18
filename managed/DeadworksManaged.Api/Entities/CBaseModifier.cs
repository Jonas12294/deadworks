namespace DeadworksManaged.Api;

/// <summary>Wraps a native CBaseModifier instance — a buff/debuff applied to an entity via <see cref="CBaseEntity.AddModifier"/>.</summary>
public unsafe class CBaseModifier : NativeEntity {
	internal CBaseModifier(nint handle) : base(handle) { }
	private static ReadOnlySpan<byte> Class => "CBaseModifier"u8;

	private static readonly SchemaAccessor<int> _serialNumber = new(Class, "m_nSerialNumber"u8);
	/// <summary>Unique serial number identifying this modifier instance.</summary>
	public int SerialNumber => _serialNumber.Get(Handle);

	private static readonly SchemaAccessor<float> _lastAppliedTime = new(Class, "m_flLastAppliedTime"u8);
	/// <summary>The game time at which this modifier was last applied/refreshed.</summary>
	public float LastAppliedTime => _lastAppliedTime.Get(Handle);

	private static readonly SchemaAccessor<float> _creationTime = new(Class, "m_flCreationTime"u8);
	/// <summary>The game time at which this modifier was created.</summary>
	public float CreationTime => _creationTime.Get(Handle);

	private static readonly SchemaAccessor<float> _duration = new(Class, "m_flDuration"u8);
	/// <summary>The base duration of this modifier in seconds. -1 means infinite.</summary>
	public float Duration { get => _duration.Get(Handle); set => _duration.Set(Handle, value); }

	private static readonly SchemaAccessor<uint> _hCaster = new(Class, "m_hCaster"u8);
	/// <summary>The entity that applied this modifier.</summary>
	public CBaseEntity? Caster {
		get {
			uint handle = _hCaster.Get(Handle);
			if (handle == 0xFFFFFFFF) return null;
			void* ptr = NativeInterop.GetEntityFromHandle(handle);
			return ptr != null ? new CBaseEntity((nint)ptr) : null;
		}
	}

	private static readonly SchemaAccessor<uint> _hAbility = new(Class, "m_hAbility"u8);
	/// <summary>The ability entity that created this modifier, or null if none.</summary>
	public CBaseEntity? Ability {
		get {
			uint handle = _hAbility.Get(Handle);
			if (handle == 0xFFFFFFFF) return null;
			void* ptr = NativeInterop.GetEntityFromHandle(handle);
			return ptr != null ? new CBaseEntity((nint)ptr) : null;
		}
	}

	private static readonly SchemaAccessor<byte> _attributes = new(Class, "m_iAttributes"u8);
	/// <summary>Modifier attribute flags.</summary>
	public byte Attributes { get => _attributes.Get(Handle); set => _attributes.Set(Handle, value); }

	private static readonly SchemaAccessor<byte> _team = new(Class, "m_iTeam"u8);
	/// <summary>The team this modifier belongs to.</summary>
	public int Team { get => _team.Get(Handle); set => _team.Set(Handle, (byte)value); }

	private static readonly SchemaAccessor<short> _stackCount = new(Class, "m_iStackCount"u8);
	/// <summary>The current stack count of this modifier.</summary>
	public int StackCount { get => _stackCount.Get(Handle); set => _stackCount.Set(Handle, (short)value); }

	private static readonly SchemaAccessor<short> _maxStackCount = new(Class, "m_iMaxStackCount"u8);
	/// <summary>The maximum stack count of this modifier.</summary>
	public int MaxStackCount { get => _maxStackCount.Get(Handle); set => _maxStackCount.Set(Handle, (short)value); }

	private static readonly SchemaAccessor<byte> _destroyReason = new(Class, "m_eDestroyReason"u8);
	/// <summary>The reason this modifier was destroyed (0 if still active).</summary>
	public byte DestroyReason => _destroyReason.Get(Handle);

	private static readonly SchemaAccessor<bool> _disabled = new(Class, "m_bDisabled"u8);
	/// <summary>Whether this modifier is currently disabled (still present but not applying effects).</summary>
	public bool Disabled { get => _disabled.Get(Handle); set => _disabled.Set(Handle, value); }

	private static readonly SchemaAccessor<float> _thinkInterval = new(Class, "m_flThinkInterval"u8);
	/// <summary>The interval between think ticks for this modifier.</summary>
	public float ThinkInterval { get => _thinkInterval.Get(Handle); set => _thinkInterval.Set(Handle, value); }

	private static readonly SchemaAccessor<float> _timeScale = new(Class, "m_flTimeScale"u8);
	/// <summary>Time scale multiplier for this modifier's duration/effects.</summary>
	public float TimeScale { get => _timeScale.Get(Handle); set => _timeScale.Set(Handle, value); }

	/// <summary>Returns how long this modifier has been active given the current game time.</summary>
	public float GetElapsedTime(float currentGameTime) {
		return currentGameTime - CreationTime;
	}

	/// <summary>Returns the remaining time based on duration and creation time. Returns -1 if infinite.</summary>
	public float GetRemainingTime(float currentGameTime) {
		float dur = Duration;
		if (dur < 0) return -1f;
		float remaining = (CreationTime + dur) - currentGameTime;
		return remaining > 0 ? remaining : 0;
	}
}
