using System.Globalization;

namespace DeadworksManaged.Api;

/// <summary>
/// Safe readers for the values a <see cref="UI.On"/> handler receives. Action
/// payloads are typed by a client, so every field must be treated like chat
/// input — these make the validated read the short read, so handlers stop
/// hand-rolling parse + clamp + whitelist:
/// <code>
/// app.On("scoreboard.sort", (slot, data) =>
/// {
///     string column = data.OneOf("column", "kills", "deaths", "networth");
///     int page = data.Int("page", 0, 9, fallback: 0);
/// });
/// </code>
/// </summary>
public static class UiActionData
{
	/// <summary>
	/// Reads an integer: <paramref name="fallback"/> when missing or not a
	/// number, clamped into [<paramref name="min"/>, <paramref name="max"/>]
	/// otherwise — a client can never push a value outside the range.
	/// </summary>
	public static int Int(this IReadOnlyDictionary<string, string> data, string key,
		int min, int max, int fallback = 0)
	{
		ArgumentNullException.ThrowIfNull(data);
		if (!data.TryGetValue(key, out var raw)
			|| !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
			return fallback;
		return Math.Clamp(value, min, max);
	}

	/// <summary>
	/// Whitelists a string: the value when it is one of
	/// <paramref name="allowed"/>, else the first entry — so the default is
	/// declared once, in the list, and unknown client input can never leak
	/// through.
	/// </summary>
	public static string OneOf(this IReadOnlyDictionary<string, string> data, string key,
		params string[] allowed)
	{
		ArgumentNullException.ThrowIfNull(data);
		if (allowed is null || allowed.Length == 0)
			throw new ArgumentException("OneOf needs at least one allowed value (the first is the default).",
				nameof(allowed));

		if (data.TryGetValue(key, out var value) && Array.IndexOf(allowed, value) >= 0)
			return value;
		return allowed[0];
	}

	/// <summary>
	/// Reads a boolean: <c>1</c>/<c>true</c> and <c>0</c>/<c>false</c> are
	/// recognised; anything else — including a missing key — is
	/// <paramref name="fallback"/>.
	/// </summary>
	public static bool Flag(this IReadOnlyDictionary<string, string> data, string key,
		bool fallback = false)
	{
		ArgumentNullException.ThrowIfNull(data);
		if (!data.TryGetValue(key, out var raw))
			return fallback;
		return raw switch
		{
			"1" or "true" => true,
			"0" or "false" => false,
			_ => fallback,
		};
	}

	/// <summary>
	/// Reads free text: trimmed and hard-capped at
	/// <paramref name="maxLength"/> characters (empty when missing). Line
	/// breaks are already impossible on this channel; the cap stops a client
	/// from storing unbounded strings in your state.
	/// </summary>
	public static string Text(this IReadOnlyDictionary<string, string> data, string key,
		int maxLength = 128)
	{
		ArgumentNullException.ThrowIfNull(data);
		if (!data.TryGetValue(key, out var raw))
			return "";
		string value = raw.Trim();
		return value.Length <= maxLength ? value : value[..maxLength];
	}
}
