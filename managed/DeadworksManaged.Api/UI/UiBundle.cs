using System.Text.Json;

namespace DeadworksManaged.Api;

/// <summary>
/// A package of Panorama content the server offers to clients. The client
/// downloads it over HTTP, mounts it ahead of the game's own content, and
/// reloads any panels named by <see cref="CacheKeys"/>.
/// </summary>
/// <remarks>
/// The bundle must be a VPK. Loose files cannot override packed game content —
/// Source 2 orders every VPK ahead of every loose directory in a search path,
/// so a loose file is never consulted for a path that also exists in
/// <c>pak01</c>.
/// </remarks>
public sealed class UiBundle
{
	/// <summary>Stable identifier for this bundle. Publishing the same id again replaces the previous version.</summary>
	public required string Id { get; init; }

	/// <summary>HTTPS URL the client downloads the VPK from.</summary>
	public required string Url { get; init; }

	/// <summary>Lowercase hex SHA-256 of the VPK. The client verifies this and reuses a cached copy when it matches.</summary>
	public required string Sha256 { get; init; }

	/// <summary>
	/// Panels to reload once the bundle is mounted, as Panorama cache keys:
	/// the <em>source</em> path with backslashes, e.g. <c>panorama\layout\hud_scoreboard.xml</c> —
	/// not the compiled <c>.vxml_c</c> name.
	/// </summary>
	/// <remarks>
	/// Defaults to <c>hud_health</c> — the proven reload target every shipped
	/// bundle patches. Set explicitly for bundles that replace other panels, or
	/// to empty to mount without reloading anything (the content then applies
	/// the next time each panel happens to load).
	/// </remarks>
	public IReadOnlyList<string> CacheKeys { get; init; } = [@"panorama\layout\hud_health.xml"];

	/// <summary>
	/// Loads a bundle from the manifest JSON that <c>build_bundle.py</c> writes
	/// next to the VPK (<c>{"id", "url", "sha256", "cacheKeys"}</c>) — so a
	/// rebuild is "copy the manifest", never "re-paste three values".
	/// </summary>
	/// <param name="path">Path to the manifest file.</param>
	/// <exception cref="ArgumentException">The manifest is malformed or missing fields; the message names the file.</exception>
	public static UiBundle FromManifest(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new ArgumentException("A manifest path is required.", nameof(path));

		Manifest? parsed;
		try
		{
			parsed = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), ManifestJson);
		}
		catch (JsonException ex)
		{
			throw new ArgumentException($"Bundle manifest '{path}' is not valid JSON: {ex.Message}");
		}
		if (parsed is null)
			throw new ArgumentException($"Bundle manifest '{path}' is empty.");

		var bundle = new UiBundle
		{
			Id = parsed.Id ?? "",
			Url = parsed.Url ?? "",
			Sha256 = (parsed.Sha256 ?? "").Trim().ToLowerInvariant(),
			CacheKeys = parsed.CacheKeys ?? [@"panorama\layout\hud_health.xml"],
		};

		try
		{
			bundle.Validate();
		}
		catch (ArgumentException ex)
		{
			throw new ArgumentException($"Bundle manifest '{path}': {ex.Message}");
		}
		return bundle;
	}

	private sealed record Manifest(string? Id, string? Url, string? Sha256, string[]? CacheKeys);

	private static readonly JsonSerializerOptions ManifestJson = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
	};

	internal void Validate()
	{
		Require(Id, nameof(Id));
		Require(Url, nameof(Url));
		Require(Sha256, nameof(Sha256));

		if (!Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
			throw new ArgumentException($"Bundle '{Id}' must be served over https.", nameof(Url));

		if (Sha256.Length != 64 || !IsHex(Sha256))
			throw new ArgumentException($"Bundle '{Id}' needs a 64-character hex SHA-256.", nameof(Sha256));

		foreach (var key in CacheKeys)
		{
			if (string.IsNullOrWhiteSpace(key))
				throw new ArgumentException($"Bundle '{Id}' has an empty cache key.", nameof(CacheKeys));
			if (key.IndexOf('/') >= 0)
				throw new ArgumentException(
					$"Cache key '{key}' uses forward slashes. Panorama keys use backslashes, " +
					"e.g. panorama\\layout\\hud_scoreboard.xml", nameof(CacheKeys));
		}
	}

	private static void Require(string value, string name)
	{
		if (string.IsNullOrWhiteSpace(value))
			throw new ArgumentException($"{name} is required.", name);
	}

	private static bool IsHex(string text)
	{
		foreach (char c in text)
		{
			bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
			if (!ok) return false;
		}
		return true;
	}
}
