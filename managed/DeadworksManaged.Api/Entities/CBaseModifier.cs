namespace DeadworksManaged.Api;

/// <summary>Wraps a native CBaseModifier instance — a buff/debuff applied to an entity via <see cref="CBaseEntity.AddModifier"/>.</summary>
public unsafe class CBaseModifier : NativeEntity {
	internal CBaseModifier(nint handle) : base(handle) { }

	/// <summary>The modifier's subclass VData (contains name, duration, etc.), or null if unavailable.</summary>
	public CModifierVData? SubclassVData {
		get {
			// m_pSubclassVData is a datamap field at offset 0x10 in CBaseModifier
			nint pVData = *(nint*)((byte*)Handle + 0x10);
			return pVData != 0 ? new CModifierVData(pVData) : null;
		}
	}
}
