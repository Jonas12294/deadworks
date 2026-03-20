namespace DeadworksManaged.Api;

/// <summary>Wraps a native CCitadelModifierVData instance — Citadel-specific modifier VData with extended properties.</summary>
public unsafe class CCitadelModifierVData : CModifierVData {
	internal CCitadelModifierVData(nint handle) : base(handle) { }

	private static readonly SchemaAccessor<byte> _isBuildup = new("CCitadelModifierVData"u8, "m_bIsBuildup"u8);
	private static readonly SchemaAccessor<byte> _durationCanBeTimeScaled = new("CCitadelModifierVData"u8, "m_bDurationCanBeTimeScaled"u8);
	private static readonly SchemaAccessor<byte> _durationReducible = new("CCitadelModifierVData"u8, "m_bDurationReducible"u8);
	private static readonly SchemaAccessor<byte> _durationAffectedByEffectiveness = new("CCitadelModifierVData"u8, "m_bDurationAffectedByEffectiveness"u8);
	private static readonly SchemaAccessor<byte> _removeOnInterrupted = new("CCitadelModifierVData"u8, "m_bRemoveOnInterrupted"u8);

	public bool IsBuildup => _isBuildup.Get(Handle) != 0;
	public bool DurationCanBeTimeScaled => _durationCanBeTimeScaled.Get(Handle) != 0;
	public bool DurationReducible => _durationReducible.Get(Handle) != 0;
	public bool DurationAffectedByEffectiveness => _durationAffectedByEffectiveness.Get(Handle) != 0;
	public bool RemoveOnInterrupted => _removeOnInterrupted.Get(Handle) != 0;
}
