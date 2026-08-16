namespace DeadworksManaged.Api;

/// <summary>
/// One node of a server-built panel tree, rendered on clients by the host
/// bundle (<c>examples/ui/host/</c>) — no CSDK, no compiling, live updates.
/// Build trees with the <see cref="Ui"/> factories and push them with
/// <see cref="UiApp.SetTree"/> or <see cref="UiApp.ServeMarkupFile"/>.
/// </summary>
/// <remarks>
/// <para><see cref="TypeName"/> is a Panorama panel type — <c>Panel</c>,
/// <c>Label</c>, <c>Button</c>, <c>Image</c>, <c>TextEntry</c>, or any other
/// the client can create. The host runtime calls <c>$.CreatePanel(TypeName,…)</c>,
/// so the set is open-ended.</para>
/// <para>Fluent setters mutate and return the same node. Styling is inline
/// style strings only (<c>"flow-children: down; width: 300px;"</c>) — the
/// runtime assigns them property by property; stylesheets cannot be delivered
/// this way.</para>
/// </remarks>
public sealed class UiNode
{
	/// <summary>The Panorama panel type the runtime creates for this node.</summary>
	public string TypeName { get; }

	/// <summary>Panel id — needed to target the node with <see cref="UiApp.UpdateNode"/>.</summary>
	public string? NodeId { get; private set; }

	/// <summary>Label/button caption, or a text entry's initial value.</summary>
	public string? Text { get; private set; }

	/// <summary>Inline style string, e.g. <c>"flow-children: right; margin-top: 4px;"</c>.</summary>
	public string? StyleText { get; private set; }

	/// <summary>Space-separated CSS classes for stylesheet matching.</summary>
	public string? ClassName { get; private set; }

	/// <summary>
	/// Event sent to the server when this node is activated — arrives through
	/// <see cref="UiApp.On"/> with the node's id in <c>d.id</c>. For local-only
	/// behaviour, wire the click in pushed JavaScript instead.
	/// </summary>
	public string? OnClickEvent { get; private set; }

	/// <summary>Image source, e.g. <c>s2r://panorama/images/…</c>.</summary>
	public string? Src { get; private set; }

	/// <summary>
	/// Style applied while the cursor is over the node; the base style is
	/// restored on mouse-out. Pure client-side — no server round trip.
	/// In markup, from a <c>hoverstyle</c> attribute or <c>:hover</c> CSS rules.
	/// </summary>
	public string? HoverStyleText { get; private set; }

	/// <summary>Event sent to the server when the cursor enters the node (<c>d.id</c> = node id).</summary>
	public string? OnHoverEvent { get; private set; }

	/// <summary>Event sent to the server when the cursor leaves the node.</summary>
	public string? OnHoverOutEvent { get; private set; }

	/// <summary>
	/// For buttons: the id of a panel whose named <c>TextEntry</c> descendants
	/// are read on click and sent along with <see cref="OnClickEvent"/> —
	/// each input's text under its own id in the action payload. This is how
	/// form input reaches the server without hand-written JS.
	/// </summary>
	public string? SubmitScopeId { get; private set; }

	/// <summary>Whether the panel starts visible. Defaults to <see langword="true"/>.</summary>
	public bool IsVisible { get; private set; } = true;

	/// <summary>Child nodes, in render order.</summary>
	public IReadOnlyList<UiNode> Children => _children;

	private readonly List<UiNode> _children = new();

	internal UiNode(string typeName)
	{
		if (string.IsNullOrWhiteSpace(typeName))
			throw new ArgumentException("A node needs a Panorama type name.", nameof(typeName));
		TypeName = typeName;
	}

	/// <summary>Sets the panel id (for later <see cref="UiApp.UpdateNode"/> / lookups).</summary>
	public UiNode Id(string id) { NodeId = id; return this; }

	/// <summary>Sets the text (labels, buttons, text entries).</summary>
	public UiNode WithText(string text) { Text = text; return this; }

	/// <summary>Sets the inline style string.</summary>
	public UiNode Style(string style) { StyleText = style; return this; }

	/// <summary>Sets the CSS class(es).</summary>
	public UiNode Class(string className) { ClassName = className; return this; }

	/// <summary>Makes the node send this event to the server when activated.</summary>
	public UiNode OnClick(string eventName) { OnClickEvent = eventName; return this; }

	/// <summary>Sets the image source.</summary>
	public UiNode WithSrc(string src) { Src = src; return this; }

	/// <summary>Sets the style shown while hovered (base style restored on mouse-out).</summary>
	public UiNode HoverStyle(string style) { HoverStyleText = style; return this; }

	/// <summary>Makes the node send this event to the server when the cursor enters it.</summary>
	public UiNode OnHover(string eventName) { OnHoverEvent = eventName; return this; }

	/// <summary>Makes the node send this event to the server when the cursor leaves it.</summary>
	public UiNode OnHoverOut(string eventName) { OnHoverOutEvent = eventName; return this; }

	/// <summary>On click, also submit the named inputs under the panel with this id.</summary>
	public UiNode SubmitFrom(string panelId) { SubmitScopeId = panelId; return this; }

	/// <summary>Sets initial visibility.</summary>
	public UiNode Visible(bool visible) { IsVisible = visible; return this; }

	/// <summary>Appends children, in order.</summary>
	public UiNode Add(params UiNode[] children)
	{
		foreach (var child in children)
		{
			ArgumentNullException.ThrowIfNull(child);
			_children.Add(child);
		}
		return this;
	}
}

/// <summary>Factories for server-built UI trees. See <see cref="UiNode"/>.</summary>
public static class Ui
{
	/// <summary>A plain container panel.</summary>
	public static UiNode Panel() => new("Panel");

	/// <summary>A text label.</summary>
	public static UiNode Label(string text = "") => new UiNode("Label").WithText(text);

	/// <summary>A button; give it <see cref="UiNode.OnClick"/> or wire it in JS.</summary>
	public static UiNode Button(string text = "") => new UiNode("Button").WithText(text);

	/// <summary>An image.</summary>
	public static UiNode Image(string src = "") => new UiNode("Image").WithSrc(src);

	/// <summary>A single-line text input. Read its value in pushed JS via <c>panel.text</c>.</summary>
	public static UiNode TextEntry(string text = "") => new UiNode("TextEntry").WithText(text);

	/// <summary>Any other Panorama panel type by name (e.g. <c>ToggleButton</c>, <c>Slider</c>).</summary>
	public static UiNode Widget(string typeName) => new(typeName);
}
