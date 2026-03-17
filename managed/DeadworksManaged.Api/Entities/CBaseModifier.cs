namespace DeadworksManaged.Api;

/// <summary>Wraps a native CBaseModifier instance — a buff/debuff applied to an entity via <see cref="CBaseEntity.AddModifier"/>.</summary>
public unsafe class CBaseModifier : NativeEntity {
	internal CBaseModifier(nint handle) : base(handle) { }
	private static ReadOnlySpan<byte> Class => "CBaseModifier"u8;

	private static readonly SchemaAccessor<float> _duration = new(Class, "m_flDuration"u8);
	/// <summary>The base duration of this modifier in seconds. -1 means infinite.</summary>
	public float Duration { get => _duration.Get(Handle); set => _duration.Set(Handle, value); }

	private static readonly SchemaAccessor<float> _dieTime = new(Class, "m_flDieTime"u8);
	/// <summary>The game time at which this modifier will expire. Set to -1 for infinite duration.</summary>
	public float DieTime { get => _dieTime.Get(Handle); set => _dieTime.Set(Handle, value); }

	private static readonly SchemaAccessor<float> _creationTime = new(Class, "m_flCreationTime"u8);
	/// <summary>The game time at which this modifier was created.</summary>
	public float CreationTime => _creationTime.Get(Handle);

	private static readonly SchemaAccessor<int> _stackCount = new(Class, "m_nStackCount"u8);
	/// <summary>The current stack count of this modifier.</summary>
	public int StackCount { get => _stackCount.Get(Handle); set => _stackCount.Set(Handle, value); }

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

	private static readonly SchemaAccessor<int> _serialNumber = new(Class, "m_iSerialNumber"u8);
	/// <summary>Unique serial number identifying this modifier instance.</summary>
	public int SerialNumber => _serialNumber.Get(Handle);

	private static readonly SchemaAccessor<bool> _disabled = new(Class, "m_bDisabled"u8);
	/// <summary>Whether this modifier is currently disabled (still present but not applying effects).</summary>
	public bool Disabled { get => _disabled.Get(Handle); set => _disabled.Set(Handle, value); }

	/// <summary>Returns the remaining time in seconds before this modifier expires given the current game time. Returns -1 if infinite.</summary>
	public float GetRemainingTime(float currentGameTime) {
		float die = DieTime;
		if (die < 0) return -1f;
		float remaining = die - currentGameTime;
		return remaining > 0 ? remaining : 0;
	}

	/// <summary>Returns how long this modifier has been active given the current game time.</summary>
	public float GetElapsedTime(float currentGameTime) {
		return currentGameTime - CreationTime;
	}
}
