using System.Globalization;

namespace DeadworksManaged.Api;

/// <summary>
/// Builds the flat row format that <see cref="UI.Emit"/> carries and
/// <c>Deadworks.SubscribeTable</c> decodes on the panel side:
/// <c>n=&lt;count&gt;</c>, then <c>r&lt;i&gt;.&lt;field&gt;=&lt;value&gt;</c> per row,
/// then any <see cref="Meta"/> keys.
/// </summary>
/// <remarks>
/// Values accept any object and are stringified with the invariant culture;
/// <see langword="null"/> becomes the empty string. Line breaks are stripped
/// from values rather than rejected — player names are the recurring source
/// of them and a silently-cleaned name beats a crashed emit. Keys are author
/// code, so a malformed key throws instead.
/// </remarks>
public sealed class UiTable
{
	/// <summary>
	/// Table-level values delivered alongside the rows (sort column, reason,
	/// …). Keys must not be <c>n</c> or look like a row key (<c>r0.</c>…),
	/// since those would collide with the row encoding.
	/// </summary>
	public Dictionary<string, object?> Meta { get; } = new();

	private readonly List<List<KeyValuePair<string, string>>> _rows = new();

	/// <summary>Number of rows added so far.</summary>
	public int RowCount => _rows.Count;

	/// <summary>Appends one row. Field order is preserved.</summary>
	/// <remarks>Concrete dictionary parameter so <c>AddRow(new() { ["k"] = 5 })</c> target-types.</remarks>
	/// <returns>This table, for chaining.</returns>
	public UiTable AddRow(Dictionary<string, object?> fields)
	{
		ArgumentNullException.ThrowIfNull(fields);

		var row = new List<KeyValuePair<string, string>>(fields.Count);
		foreach (var (key, value) in fields)
		{
			ValidateKey(key, "row field");
			row.Add(new(key, Stringify(value)));
		}
		_rows.Add(row);
		return this;
	}

	/// <summary>
	/// Flattens the table into the dictionary <see cref="UI.Emit"/> and
	/// <see cref="UiApp.Emit"/> take.
	/// </summary>
	public Dictionary<string, string> ToData()
	{
		var data = new Dictionary<string, string>
		{
			["n"] = _rows.Count.ToString(CultureInfo.InvariantCulture),
		};

		for (int i = 0; i < _rows.Count; i++)
			foreach (var (field, value) in _rows[i])
				data["r" + i + "." + field] = value;

		foreach (var (key, value) in Meta)
		{
			ValidateKey(key, "meta");
			if (key == "n" || IsRowKey(key))
				throw new ArgumentException(
					$"Meta key '{key}' collides with the row encoding ('n' and 'r<i>.…' are reserved).");
			data[key] = Stringify(value);
		}

		return data;
	}

	private static void ValidateKey(string key, string kind)
	{
		if (string.IsNullOrEmpty(key))
			throw new ArgumentException($"A {kind} key must not be empty.");
		if (key.IndexOf('=') >= 0 || key.IndexOf('\n') >= 0 || key.IndexOf('\r') >= 0)
			throw new ArgumentException($"The {kind} key '{key}' must not contain '=' or line breaks.");
	}

	private static bool IsRowKey(string key)
	{
		// r<digits>. — the prefix ToData writes for row fields.
		if (key.Length < 3 || key[0] != 'r') return false;
		int i = 1;
		while (i < key.Length && char.IsAsciiDigit(key[i])) i++;
		return i > 1 && i < key.Length && key[i] == '.';
	}

	private static string Stringify(object? value)
	{
		string text = value switch
		{
			null => "",
			string s => s,
			IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
			_ => value.ToString() ?? "",
		};
		return text.IndexOf('\n') >= 0 || text.IndexOf('\r') >= 0
			? text.Replace("\r", "").Replace("\n", " ")
			: text;
	}
}
