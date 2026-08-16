using System.Globalization;

namespace DeadworksManaged.Api;

/// <summary>
/// Serialises a <see cref="UiNode"/> tree into the chunked rows-as-nodes wire
/// format the host runtime (<c>dwhost1.js</c>) rebuilds panels from.
/// </summary>
/// <remarks>
/// One <see cref="UiTable"/> row per node, depth-first, empty fields omitted:
/// <c>t</c> type, <c>p</c> parent index (<c>-1</c> = root), <c>id</c>,
/// <c>txt</c>, <c>sty</c>, <c>act</c> (on-click event), <c>src</c>,
/// <c>vis</c> (<c>0</c> only when hidden). Chunks are packed greedily up to a
/// byte budget — the message-148 ceiling is untested beyond ~1 KB, so each
/// emit stays conservatively below it. Node indices are global: concatenating
/// chunk rows in order restores document order, which the <c>p</c> indices
/// rely on. Chunk meta: <c>rev</c> / <c>chunk</c> / <c>of</c>.
/// </remarks>
internal static class UiTreeCodec
{
	internal const string SetEvent = "dw.tree.set";
	internal const string UpdateEvent = "dw.tree.update";

	/// <summary>Payload budget per emit, in estimated bytes.</summary>
	internal const int ChunkBudgetBytes = 1200;

	/// <summary>Flattens a tree and packs it into one table per chunk.</summary>
	internal static List<UiTable> Encode(UiNode root, int rev, string? imagePrefix = null)
		=> Encode([(root, imagePrefix)], rev);

	/// <summary>
	/// Flattens several trees into one wire tree — the composition several
	/// apps sharing one panel produce. Every root is emitted with parent
	/// <c>-1</c>, which the host runtime already builds as a direct child of
	/// its container, so a forest needs nothing new on the client.
	/// </summary>
	/// <param name="roots">
	/// Each root with the <c>dw://</c> namespace its owner's images live under
	/// (null for an app serving no images). The prefix travels per subtree
	/// because a composition mixes apps.
	/// </param>
	/// <param name="rev">Revision the client reassembles chunks under.</param>
	internal static List<UiTable> Encode(IReadOnlyList<(UiNode Root, string? ImagePrefix)> roots, int rev)
	{
		ArgumentNullException.ThrowIfNull(roots);

		var rows = new List<Dictionary<string, object?>>();
		foreach (var (root, imagePrefix) in roots)
		{
			ArgumentNullException.ThrowIfNull(root);
			Flatten(root, -1, rows, imagePrefix);
		}

		// Greedy packing: start a new chunk when the next row would overflow
		// the budget. A single oversized row still ships alone rather than
		// looping forever.
		var chunks = new List<List<Dictionary<string, object?>>>();
		var current = new List<Dictionary<string, object?>>();
		int currentBytes = 0;
		foreach (var row in rows)
		{
			int rowBytes = Estimate(row);
			if (current.Count > 0 && currentBytes + rowBytes > ChunkBudgetBytes)
			{
				chunks.Add(current);
				current = new List<Dictionary<string, object?>>();
				currentBytes = 0;
			}
			current.Add(row);
			currentBytes += rowBytes;
		}
		chunks.Add(current);   // always at least one, even for a bare root

		var tables = new List<UiTable>(chunks.Count);
		for (int i = 0; i < chunks.Count; i++)
		{
			var table = new UiTable
			{
				Meta =
				{
					["rev"] = rev.ToString(CultureInfo.InvariantCulture),
					["chunk"] = i.ToString(CultureInfo.InvariantCulture),
					["of"] = chunks.Count.ToString(CultureInfo.InvariantCulture),
				},
			};
			foreach (var row in chunks[i])
				table.AddRow(row);
			tables.Add(table);
		}
		return tables;
	}

	/// <summary>One row of a <see cref="UiApp.UpdateNode"/> emit.</summary>
	internal static UiTable EncodeUpdate(string nodeId, string? text, string? style, bool? visible)
	{
		var row = new Dictionary<string, object?> { ["id"] = nodeId };
		if (text is not null) row["txt"] = text;
		if (style is not null) row["sty"] = style;
		if (visible is not null) row["vis"] = visible.Value ? "1" : "0";

		var table = new UiTable();
		table.AddRow(row);
		return table;
	}

	/// <summary>
	/// Namespaces a bare <c>dw://name.png</c> into the owning app's images
	/// folder, so two apps can each ship a <c>logo.png</c> without knowing
	/// about each other. A src that already names a folder
	/// (<c>dw://other/logo.png</c>) is left alone — that is how one app
	/// deliberately uses another's image.
	/// </summary>
	internal static string NamespaceSrc(string src, string? imagePrefix)
	{
		if (string.IsNullOrEmpty(imagePrefix) || !src.StartsWith("dw://", StringComparison.Ordinal))
			return src;

		string stem = src[5..];
		return stem.Contains('/') ? src : $"dw://{imagePrefix}/{stem}";
	}

	private static void Flatten(UiNode node, int parentIndex, List<Dictionary<string, object?>> rows,
		string? imagePrefix = null)
	{
		var row = new Dictionary<string, object?>
		{
			["t"] = node.TypeName,
			["p"] = parentIndex.ToString(CultureInfo.InvariantCulture),
		};
		if (!string.IsNullOrEmpty(node.NodeId)) row["id"] = node.NodeId;
		if (!string.IsNullOrEmpty(node.Text)) row["txt"] = node.Text;
		if (!string.IsNullOrEmpty(node.StyleText)) row["sty"] = node.StyleText;
		if (!string.IsNullOrEmpty(node.OnClickEvent)) row["act"] = node.OnClickEvent;
		if (!string.IsNullOrEmpty(node.Src)) row["src"] = NamespaceSrc(node.Src, imagePrefix);
		if (!string.IsNullOrEmpty(node.HoverStyleText)) row["hsty"] = node.HoverStyleText;
		if (!string.IsNullOrEmpty(node.OnHoverEvent)) row["hov"] = node.OnHoverEvent;
		if (!string.IsNullOrEmpty(node.OnHoverOutEvent)) row["hout"] = node.OnHoverOutEvent;
		if (!string.IsNullOrEmpty(node.SubmitScopeId)) row["sub"] = node.SubmitScopeId;
		if (!node.IsVisible) row["vis"] = "0";

		int index = rows.Count;
		rows.Add(row);
		foreach (var child in node.Children)
			Flatten(child, index, rows, imagePrefix);
	}

	private static int Estimate(Dictionary<string, object?> row)
	{
		// r<i>.<key>=<value>\n per field; the index digits and the base64
		// expansion are close enough to the slack in the budget.
		int bytes = 0;
		foreach (var (key, value) in row)
			bytes += 4 + key.Length + (value?.ToString()?.Length ?? 0) + 1;
		return bytes;
	}
}
