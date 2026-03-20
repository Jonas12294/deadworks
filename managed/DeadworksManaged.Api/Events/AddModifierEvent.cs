namespace DeadworksManaged.Api;

/// <summary>
/// Event data for the AddModifier hook. Modify properties and return <see cref="HookResult.Stop"/>
/// to block or change the modifier application.
/// </summary>
public sealed class AddModifierEvent {
	/// <summary>The CModifierProperty that the modifier is being added to.</summary>
	public required CModifierProperty ModifierProperty { get; init; }

	/// <summary>The entity casting the modifier. Can be reassigned to change the caster.</summary>
	public required CBaseEntity Caster { get; set; }

	/// <summary>The ability entity handle (CHandle). 0xFFFFFFFF if no ability.</summary>
	public required uint AbilityHandle { get; set; }

	/// <summary>The team number. Can be reassigned.</summary>
	public required int Team { get; set; }

	/// <summary>The modifier VData being applied.</summary>
	public required CCitadelModifierVData ModifierVData { get; init; }

	/// <summary>Modifier parameters (KeyValues3). May be null if none were provided.</summary>
	public KeyValues3? ModifierParams { get; init; }

	/// <summary>Key values (KeyValues3). May be null if none were provided.</summary>
	public KeyValues3? KeyValues { get; init; }

	/// <summary>The ability entity resolved from AbilityHandle, or null if invalid.</summary>
	public CBaseEntity? Ability => CBaseEntity.FromHandle(AbilityHandle);
}
