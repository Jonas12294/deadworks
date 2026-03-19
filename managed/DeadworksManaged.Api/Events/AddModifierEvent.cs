namespace DeadworksManaged.Api;

/// <summary>Event data passed to <see cref="IDeadworksPlugin.OnAddModifier"/>. Contains the target modifier property, caster, ability, and modifier name.</summary>
public sealed class AddModifierEvent {
	/// <summary>Pointer to the target entity's CModifierProperty (the entity receiving the modifier).</summary>
	public required nint ModifierPropHandle { get; init; }
	/// <summary>The entity applying the modifier (caster). May be the same as the target for self-applied modifiers.</summary>
	public required CBaseEntity? Caster { get; init; }
	/// <summary>The ability entity that triggered this modifier, or null if not from an ability.</summary>
	public required CBaseEntity? Ability { get; init; }
	/// <summary>VData name of the modifier being applied (e.g. "modifier_citadel_knockdown").</summary>
	public required string ModifierName { get; init; }
	/// <summary>Debuff type from CModifierVData: 0 = debuff if from enemy, 1 = always debuff, 2 = never debuff.</summary>
	public required int DebuffType { get; init; }
	/// <summary>The team value passed to AddModifier.</summary>
	public required int Team { get; init; }

	/// <summary>Convenience wrapper: returns a CModifierProperty from the raw handle.</summary>
	public CModifierProperty? ModifierProp =>
		ModifierPropHandle != 0 ? new CModifierProperty(ModifierPropHandle) : null;
}
