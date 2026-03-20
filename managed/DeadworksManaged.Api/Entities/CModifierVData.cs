namespace DeadworksManaged.Api;

/// <summary>Wraps a native CModifierVData instance — base VData for all modifier types.</summary>
public unsafe class CModifierVData : NativeEntity {
	internal CModifierVData(nint handle) : base(handle) { }

	private static readonly SchemaAccessor<float> _duration = new("CModifierVData"u8, "m_flDuration"u8);
	private static readonly SchemaAccessor<byte> _isHidden = new("CModifierVData"u8, "m_bIsHidden"u8);

	/// <summary>Default duration of the modifier (from VData).</summary>
	public float Duration => _duration.Get(Handle);

	/// <summary>Whether this modifier is hidden from the HUD.</summary>
	public bool IsHidden => _isHidden.Get(Handle) != 0;

	/// <summary>The subclass definition name (e.g. "modifier_citadel_knockdown").</summary>
	public string Name {
		get {
			nint namePtr = *(nint*)((byte*)Handle + 0x10);
			return namePtr != 0 ? System.Runtime.InteropServices.Marshal.PtrToStringUTF8(namePtr) ?? "" : "";
		}
	}
}
