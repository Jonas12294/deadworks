using System.Text;
using System.Xml.Linq;

namespace DeadworksManaged.Api;

/// <summary>
/// Parses raw markup (XML) and CSS text into a <see cref="UiNode"/> tree — the
/// "push raw HTML/CSS from the server" path. The result rides the same
/// transport and host renderer as <see cref="UiApp.SetTree"/>, so authors edit
/// real <c>.xml</c>/<c>.css</c> files with no compiler and no hosting.
/// </summary>
/// <remarks>
/// <para>Tags map to Panorama panel types (case-insensitive): <c>panel</c>/<c>div</c>
/// → Panel, <c>label</c>/<c>span</c>/<c>text</c> → Label, <c>button</c> →
/// Button, <c>img</c>/<c>image</c> → Image, <c>input</c>/<c>textentry</c> →
/// TextEntry. Any other tag is passed through as a Panorama type by name
/// (e.g. <c>&lt;ToggleButton&gt;</c>), so the widget set is open-ended.
/// Attributes: <c>id</c>, <c>text</c> (or inner text), <c>style</c>,
/// <c>class</c>, <c>src</c>, <c>visible="false"</c>, and
/// <c>onclick</c>/<c>onactivate</c> (the server event a click sends; for
/// local-only behaviour wire the click in pushed JS instead).</para>
/// <para>CSS supports <c>*</c>, tag, <c>.class</c> and <c>#id</c> selectors
/// (no combinators). Matching rules are merged by specificity then source
/// order, inline <c>style</c> winning last, and baked into each node's style
/// string — so the host renderer needs no CSS knowledge.</para>
/// </remarks>
public static class UiMarkup
{
	/// <summary>Parses markup, optionally styled by a CSS string, into a tree.</summary>
	/// <exception cref="FormatException">The markup is not well-formed.</exception>
	public static UiNode Parse(string xml, string? css = null)
	{
		if (string.IsNullOrWhiteSpace(xml))
			throw new FormatException("Markup is empty.");

		XDocument doc;
		try
		{
			doc = XDocument.Parse(xml);
		}
		catch (System.Xml.XmlException ex)
		{
			throw new FormatException($"Markup is not well-formed XML: {ex.Message}", ex);
		}

		var rules = CssRule.ParseSheet(css);
		return FromElement(doc.Root!, rules);
	}

	/// <summary>Resolves a markup tag to a Panorama panel type name.</summary>
	internal static string TypeForTag(string tag) => tag.ToLowerInvariant() switch
	{
		"panel" or "div" or "root" => "Panel",
		"label" or "span" or "text" => "Label",
		"button" => "Button",
		"img" or "image" => "Image",
		"input" or "textentry" => "TextEntry",
		// Passthrough: use the tag verbatim as a Panorama type (e.g. ToggleButton).
		_ => tag,
	};

	private static UiNode FromElement(XElement element, List<CssRule> rules)
	{
		string type = TypeForTag(element.Name.LocalName);
		var node = new UiNode(type);

		string? id = Attr(element, "id");
		string? cls = Attr(element, "class");
		if (id is not null) node.Id(id);
		if (cls is not null) node.Class(cls);
		if (Attr(element, "src") is { } src) node.WithSrc(src);
		if (Attr(element, "onclick") is { } click) node.OnClick(click);
		else if (Attr(element, "onactivate") is { } activate) node.OnClick(activate);
		if (Attr(element, "onmouseover") is { } over) node.OnHover(over);
		if (Attr(element, "onmouseout") is { } outEv) node.OnHoverOut(outEv);
		if (Attr(element, "submit") is { } submit) node.SubmitFrom(submit);
		if (string.Equals(Attr(element, "visible"), "false", StringComparison.OrdinalIgnoreCase))
			node.Visible(false);

		// Text: explicit attribute, else the element's own text when it has no
		// child elements (so <label>Hello</label> works).
		if (Attr(element, "text") is { } text)
			node.WithText(text);
		else if (!element.HasElements)
		{
			string inner = element.Value.Trim();
			if (inner.Length > 0) node.WithText(inner);
		}

		// Computed style: matching CSS rules (by specificity, then order), then
		// the inline style attribute last. Hover rules (`:hover` selectors, plus
		// the hoverstyle attribute last) are computed the same way but kept
		// separate — the host runtime swaps them in on mouseover.
		string computed = ComputeStyle(type, id, cls, rules, Attr(element, "style"), hover: false);
		if (computed.Length > 0) node.Style(computed);
		string hoverComputed = ComputeStyle(type, id, cls, rules, Attr(element, "hoverstyle"), hover: true);
		if (hoverComputed.Length > 0) node.HoverStyle(hoverComputed);

		foreach (var child in element.Elements())
			node.Add(FromElement(child, rules));

		return node;
	}

	private static string ComputeStyle(string type, string? id, string? cls,
		List<CssRule> rules, string? inline, bool hover)
	{
		var matched = new List<CssRule>();
		foreach (var rule in rules)
			if (rule.IsHover == hover && rule.Matches(type, id, cls))
				matched.Add(rule);
		matched.Sort((a, b) => a.Specificity != b.Specificity
			? a.Specificity - b.Specificity
			: a.Order - b.Order);

		var sb = new StringBuilder();
		foreach (var rule in matched)
			AppendDecls(sb, rule.Declarations);
		if (!string.IsNullOrWhiteSpace(inline))
			AppendDecls(sb, inline);
		return sb.ToString().Trim();
	}

	private static void AppendDecls(StringBuilder sb, string decls)
	{
		string trimmed = decls.Trim();
		if (trimmed.Length == 0) return;
		if (sb.Length > 0 && sb[^1] != ';') sb.Append("; ");
		sb.Append(trimmed);
		if (!trimmed.EndsWith(';')) sb.Append(';');
	}

	private static string? Attr(XElement element, string name)
	{
		foreach (var attr in element.Attributes())
			if (string.Equals(attr.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
				return attr.Value;
		return null;
	}

	/// <summary>One CSS rule: a single selector plus its declaration block.</summary>
	private sealed class CssRule
	{
		private enum Kind { Universal, Tag, Class, Id }

		private readonly Kind _kind;
		private readonly string _name;   // lowercased tag/class/id, empty for universal
		public string Declarations { get; }
		public int Specificity { get; }
		public int Order { get; }

		/// <summary>The selector carried a <c>:hover</c> suffix — the rule
		/// contributes to the node's hover style, never its base style.</summary>
		public bool IsHover { get; }

		private CssRule(Kind kind, string name, string decls, int order, bool isHover)
		{
			_kind = kind;
			_name = name;
			Declarations = decls;
			Order = order;
			IsHover = isHover;
			Specificity = kind switch { Kind.Id => 100, Kind.Class => 10, Kind.Tag => 1, _ => 0 };
		}

		public bool Matches(string type, string? id, string? cls) => _kind switch
		{
			Kind.Universal => true,
			Kind.Id => id is not null && string.Equals(id, _name, StringComparison.OrdinalIgnoreCase),
			Kind.Class => cls is not null && HasClass(cls, _name),
			// A tag selector matches either the resolved Panorama type or the
			// original markup alias (so `input {}` styles a TextEntry).
			Kind.Tag => string.Equals(type, _name, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(TypeForTag(_name), type, StringComparison.OrdinalIgnoreCase),
			_ => false,
		};

		private static bool HasClass(string classAttr, string want)
		{
			foreach (var c in classAttr.Split(' ', StringSplitOptions.RemoveEmptyEntries))
				if (string.Equals(c, want, StringComparison.OrdinalIgnoreCase))
					return true;
			return false;
		}

		/// <summary>Parses a whole stylesheet into a flat, source-ordered rule list.</summary>
		public static List<CssRule> ParseSheet(string? css)
		{
			var rules = new List<CssRule>();
			if (string.IsNullOrWhiteSpace(css))
				return rules;

			string text = StripComments(css);
			int order = 0;
			int i = 0;
			while (i < text.Length)
			{
				int open = text.IndexOf('{', i);
				if (open < 0) break;
				int close = text.IndexOf('}', open + 1);
				if (close < 0) break;

				string selectors = text[i..open].Trim();
				string body = text[(open + 1)..close].Trim();
				i = close + 1;

				foreach (var sel in selectors.Split(',', StringSplitOptions.RemoveEmptyEntries))
				{
					string s = sel.Trim();
					if (s.Length == 0) continue;
					bool isHover = s.EndsWith(":hover", StringComparison.OrdinalIgnoreCase);
					if (isHover) s = s[..^":hover".Length].Trim();
					if (s.Length == 0) continue;
					CssRule rule = s[0] switch
					{
						'*' => new CssRule(Kind.Universal, "", body, order, isHover),
						'.' => new CssRule(Kind.Class, s[1..].ToLowerInvariant(), body, order, isHover),
						'#' => new CssRule(Kind.Id, s[1..], body, order, isHover),
						_ => new CssRule(Kind.Tag, s.ToLowerInvariant(), body, order, isHover),
					};
					rules.Add(rule);
				}
				order++;
			}
			return rules;
		}

		private static string StripComments(string css)
		{
			var sb = new StringBuilder(css.Length);
			for (int i = 0; i < css.Length; i++)
			{
				if (i + 1 < css.Length && css[i] == '/' && css[i + 1] == '*')
				{
					int end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
					if (end < 0) break;
					i = end + 1;
				}
				else sb.Append(css[i]);
			}
			return sb.ToString();
		}
	}
}
