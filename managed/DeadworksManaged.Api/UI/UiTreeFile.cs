using System.Text.Json;

namespace DeadworksManaged.Api;

/// <summary>
/// Loads a <see cref="UiNode"/> tree from JSON — the file format behind
/// <see cref="UiApp.ServeTreeFile"/>'s edit-save-see-it-live loop. Mirrors
/// the <see cref="Ui"/> builder 1:1:
/// <code>
/// { "type": "Panel", "style": "flow-children: down;",
///   "children": [
///     { "type": "Label", "id": "msg", "text": "Hello" },
///     { "type": "Button", "text": "Ping", "onClick": "tree.ping" } ] }
/// </code>
/// Keys are case-insensitive; comments and trailing commas are allowed, since
/// the file is meant to be edited by hand.
/// </summary>
public static class UiTreeFile
{
	private static readonly JsonDocumentOptions Options = new()
	{
		CommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
	};

	/// <summary>Reads and parses a tree file.</summary>
	/// <exception cref="FormatException">The JSON does not describe a valid tree.</exception>
	public static UiNode Load(string path) => Parse(File.ReadAllText(path));

	/// <summary>Parses a tree from JSON text.</summary>
	/// <exception cref="FormatException">The JSON does not describe a valid tree.</exception>
	public static UiNode Parse(string json)
	{
		using var doc = JsonDocument.Parse(json, Options);
		return FromElement(doc.RootElement);
	}

	private static UiNode FromElement(JsonElement element)
	{
		if (element.ValueKind != JsonValueKind.Object)
			throw new FormatException($"Expected a node object, got {element.ValueKind}.");

		string? type = GetString(element, "type")
			?? throw new FormatException("A node needs a \"type\" (Panel, Label, Button, Image, TextEntry, …).");

		// Any Panorama panel type by name; the common HTML-ish aliases resolve
		// the same way as markup tags.
		UiNode node = Ui.Widget(UiMarkup.TypeForTag(type));

		if (GetString(element, "class") is { } cls) node.Class(cls);
		if (GetString(element, "id") is { } id) node.Id(id);
		if (GetString(element, "text") is { } text) node.WithText(text);
		if (GetString(element, "style") is { } style) node.Style(style);
		if (GetString(element, "onClick") is { } onClick) node.OnClick(onClick);
		if (GetString(element, "src") is { } src) node.WithSrc(src);
		if (Get(element, "visible") is { ValueKind: JsonValueKind.False }) node.Visible(false);

		if (Get(element, "children") is { ValueKind: JsonValueKind.Array } children)
			foreach (var child in children.EnumerateArray())
				node.Add(FromElement(child));

		return node;
	}

	private static JsonElement? Get(JsonElement element, string name)
	{
		foreach (var property in element.EnumerateObject())
			if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
				return property.Value;
		return null;
	}

	private static string? GetString(JsonElement element, string name)
		=> Get(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
}
