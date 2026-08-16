using System.Security.Cryptography;

namespace DeadworksManaged.Api;

/// <summary>
/// The result of packing images: the VPK bytes to deliver, its SHA-256, and
/// the <c>s2r://</c> path each image is referenced by from a panel.
/// </summary>
public sealed class UiImagePackResult
{
	/// <summary>The mountable VPK v2 containing every image as a compiled texture.</summary>
	public required byte[] Vpk { get; init; }

	/// <summary>Lowercase hex SHA-256 of <see cref="Vpk"/> (the client verifies it).</summary>
	public required string Sha256 { get; init; }

	/// <summary>Image name → the <c>s2r://…vtex</c> path a tree/markup src uses.</summary>
	public required IReadOnlyDictionary<string, string> Paths { get; init; }
}

/// <summary>
/// Turns a set of PNGs into an in-memory images VPK — decode
/// (<see cref="UiPng"/>) → compile (<see cref="UiTexture"/>) → pack
/// (<see cref="UiVpk"/>) — so a server can ship custom images with no CSDK and
/// no image tooling. Delivered over the data channel (no per-server hosting)
/// and mounted by the client module.
/// </summary>
public static class UiImagePack
{
	/// <summary>The images subfolder under <c>panorama/images/</c>; also the <c>dw://</c> root.</summary>
	public const string Subdir = "dw";

	/// <summary>
	/// Builds an images VPK from <paramref name="pngs"/> (name → PNG bytes).
	/// Names become filenames and are referenced as <c>dw://&lt;name&gt;.png</c>
	/// or the full <c>s2r://panorama/images/dw/&lt;name&gt;.vtex</c>.
	/// </summary>
	/// <exception cref="ArgumentException">Empty set, a bad name, or an undecodable PNG.</exception>
	public static UiImagePackResult Build(IReadOnlyDictionary<string, byte[]> pngs)
		=> BuildMerged([("", pngs)]);

	/// <summary>
	/// Builds one images VPK from several sets, each under its own folder —
	/// how a server with more than one image-serving app ships them, since the
	/// client mounts a single images pack. A set's folder is usually the app
	/// id, which is what makes two apps' <c>logo.png</c> different files.
	/// </summary>
	/// <param name="sets">
	/// Folder (empty for the root) and its images, name → PNG bytes. Names are
	/// plain stems; the folder namespaces them.
	/// </param>
	/// <returns>
	/// The pack, with <see cref="UiImagePackResult.Paths"/> keyed by
	/// <c>folder/name</c> (or <c>name</c> at the root).
	/// </returns>
	/// <exception cref="ArgumentException">No images at all, a bad name, or an undecodable PNG.</exception>
	public static UiImagePackResult BuildMerged(
		IReadOnlyList<(string Folder, IReadOnlyDictionary<string, byte[]> Pngs)> sets)
	{
		ArgumentNullException.ThrowIfNull(sets);

		var files = new List<(string Path, byte[] Data)>();
		var paths = new Dictionary<string, string>();

		// Ordered so the same images always produce the same bytes, and
		// therefore the same hash - the client skips a repush of a pack it
		// already has.
		foreach (var (folder, pngs) in sets.OrderBy(s => s.Folder, StringComparer.Ordinal))
		{
			ArgumentNullException.ThrowIfNull(pngs);
			if (folder.Length > 0 && !IsPlainName(folder))
				throw new ArgumentException($"Image folder '{folder}' must be a plain name.", nameof(sets));

			string dir = folder.Length > 0 ? $"{Subdir}/{folder}" : Subdir;

			foreach (var name in pngs.Keys.OrderBy(n => n, StringComparer.Ordinal))
			{
				if (!IsPlainName(name))
					throw new ArgumentException($"Image name '{name}' must be a plain file name.", nameof(sets));

				byte[] vtex;
				try
				{
					var (rgba, w, h) = UiPng.Decode(pngs[name]);
					vtex = UiTexture.EncodeVtex(rgba, w, h);
				}
				catch (Exception ex) when (ex is FormatException or ArgumentException)
				{
					throw new ArgumentException($"Image '{name}' could not be decoded: {ex.Message}", nameof(sets));
				}

				files.Add(($"panorama/images/{dir}/{name}.vtex_c", vtex));
				paths[folder.Length > 0 ? $"{folder}/{name}" : name] =
					$"s2r://panorama/images/{dir}/{name}.vtex";
			}
		}

		if (files.Count == 0)
			throw new ArgumentException("No images to pack.", nameof(sets));

		byte[] vpk = UiVpk.Build(files);
		return new UiImagePackResult
		{
			Vpk = vpk,
			Sha256 = Convert.ToHexStringLower(SHA256.HashData(vpk)),
			Paths = paths,
		};
	}

	/// <summary>A plain file-name stem: no separators, no traversal, no extension dots.</summary>
	private static bool IsPlainName(string name)
		=> !string.IsNullOrWhiteSpace(name)
			&& name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
			&& name.IndexOfAny(['\\', '/', '.']) < 0;
}
