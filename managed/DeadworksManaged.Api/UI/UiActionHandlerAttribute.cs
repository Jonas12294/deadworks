namespace DeadworksManaged.Api;

/// <summary>
/// Marks a plugin method to be auto-registered as the handler for a panel
/// action — what <c>Deadworks.SendToServer("&lt;event&gt;", ...)</c> sends from
/// Panorama script. The mirror of <see cref="GameEventHandlerAttribute"/> for
/// UI:
/// <code>
/// [UiActionHandler("myui.clicked")]
/// public void OnClicked(UiAction e)
/// {
///     Chat.PrintToChat(e.PlayerSlot, $"you clicked {e.GetString("id")}");
/// }
/// </code>
/// </summary>
/// <remarks>
/// <para>The method takes a single <see cref="UiAction"/> and returns either
/// <see langword="void"/> or <see cref="HookResult"/>; returning
/// <see cref="HookResult.Stop"/> (or <see cref="HookResult.Handled"/>) keeps
/// later handlers for the same event from running. A method that does not fit
/// either shape is skipped with a logged message rather than failing the
/// load.</para>
/// <para>Registration follows the plugin, not a <see cref="UiApp"/>: handlers
/// are live from load until unload, whether or not the plugin ever created an
/// app. <see cref="UiApp.On"/> remains for handlers registered at runtime.</para>
/// <para>Values arrive from a client and are untrusted — read them through the
/// validating accessors on <see cref="UiAction"/>.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class UiActionHandlerAttribute : Attribute
{
	/// <summary>The action name the panel sends, e.g. <c>myui.clicked</c>.</summary>
	public string EventName { get; }

	public UiActionHandlerAttribute(string eventName) => EventName = eventName;
}
