using System.Text;

namespace DeadworksManaged.Api;

/// <summary>
/// Line-oriented <c>key=value</c> payload carried in the <c>data</c> field of a
/// custom game event. Deliberately simple: the client side of this protocol is
/// C++ with no protobuf or JSON dependency, so the format has to be trivial to
/// parse there. Split is on the first <c>=</c>, so values may contain further
/// <c>=</c> characters; newlines are rejected because they are the separator.
/// </summary>
internal sealed class UiPayload
{
	/// <summary>Bumped if the wire format ever changes shape.</summary>
	internal const int Version = 1;

	private readonly List<KeyValuePair<string, string>> _entries = new();

	internal UiPayload()
	{
		_entries.Add(new("v", Version.ToString()));
	}

	/// <summary>Appends an entry. Repeating a key is meaningful — the reader keeps every occurrence.</summary>
	internal UiPayload Add(string key, string value)
	{
		if (string.IsNullOrEmpty(key))
			throw new ArgumentException("Payload key must not be empty.", nameof(key));
		if (key.IndexOf('=') >= 0)
			throw new ArgumentException($"Payload key '{key}' must not contain '='.", nameof(key));
		Reject(key, nameof(key));
		Reject(value, nameof(value));
		_entries.Add(new(key, value));
		return this;
	}

	private static void Reject(string text, string parameter)
	{
		if (text.IndexOf('\n') >= 0 || text.IndexOf('\r') >= 0)
			throw new ArgumentException("Payload values must not contain line breaks.", parameter);
	}

	internal byte[] ToBytes()
	{
		var sb = new StringBuilder();
		foreach (var (key, value) in _entries)
			sb.Append(key).Append('=').Append(value).Append('\n');
		return Encoding.UTF8.GetBytes(sb.ToString());
	}

	/// <summary>
	/// Parses a payload produced by <see cref="ToBytes"/>. Returns <see langword="null"/>
	/// if the version line is missing or unrecognised, so a future client talking to an
	/// older server fails closed rather than misreading fields.
	/// </summary>
	internal static List<KeyValuePair<string, string>>? Parse(ReadOnlySpan<byte> bytes)
	{
		string text;
		try
		{
			text = Encoding.UTF8.GetString(bytes);
		}
		catch (ArgumentException)
		{
			return null;
		}

		var entries = new List<KeyValuePair<string, string>>();
		foreach (var rawLine in text.Split('\n'))
		{
			var line = rawLine.TrimEnd('\r');
			if (line.Length == 0)
				continue;
			int split = line.IndexOf('=');
			if (split <= 0)
				continue;
			entries.Add(new(line[..split], line[(split + 1)..]));
		}

		if (entries.Count == 0 || entries[0].Key != "v" || entries[0].Value != Version.ToString())
			return null;

		return entries;
	}

	internal static string? Find(List<KeyValuePair<string, string>> entries, string key)
	{
		foreach (var entry in entries)
			if (entry.Key == key)
				return entry.Value;
		return null;
	}

	internal static List<string> FindAll(List<KeyValuePair<string, string>> entries, string key)
	{
		var values = new List<string>();
		foreach (var entry in entries)
			if (entry.Key == key)
				values.Add(entry.Value);
		return values;
	}
}
