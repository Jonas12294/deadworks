# Builds any Deadworks UI bundle: stages the sources into the CSDK content
# tree, compiles them with resourcecompiler, packs a VPK, and prints the
# SHA-256 plus a ready-to-paste plugin config snippet.
#
#   python build_bundle.py <bundle_dir>
#   python build_bundle.py examples/ui/starter --url-prefix https://my.cdn/ui/
#
# A bundle dir is self-describing:
#
#   mybundle/
#   |- bundle.json        {"name": "...", "copy": [...], "urlPrefix": "...", "outDir": "..."}
#   `- panorama/          layout/, scripts/, (styles/) - the sources, verbatim
#
# bundle.json fields (all optional except name):
#   name       addon name; also the VPK filename prefix
#   copy       extra files staged into the tree, e.g. the deadworks.js helper
#              under its versioned name:
#              [{"from": "../deadworks.js", "to": "panorama/scripts/dwcore1.js"}]
#              ("from" is relative to the bundle dir)
#   urlPrefix  base URL used in the printed config snippet
#   outDir     where the VPK lands, relative to the bundle dir (default "dist")
#
# Machine paths default to this box's layout; override with the flags below.
# Only game/bin_cs2/win64/resourcecompiler.exe works - the CSDK's default bin
# and bin_server compilers abort with schema mismatches.

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))

# examples/ui -> examples -> deadworks-sdk -> Deadworks-testing-stuff
DEFAULT_TESTING_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(HERE)))
DEFAULT_CSDK = r"C:\Users\laurs\Downloads\Reduced_CSDK_12"
DEFAULT_URL_PREFIX = "https://glutensnake.com/ui/"

# Compile order matters: a layout's <scripts> includes reference the compiled
# script names, so scripts (and styles) go first. Images before layout too.
COMPILE_ORDER = ["scripts", "styles", "images", "layout"]

# Image files are compiled to textures via a generated .vtex definition (the
# compiler cannot ingest a bare .png). Crisp RGBA for UI, no mips.
IMAGE_EXTS = (".png", ".jpg", ".jpeg", ".tga", ".psd")

VTEX_TEMPLATE = '''<!-- dmx encoding keyvalues2_noids 1 format vtex 1 -->
"CDmeVtex"
{{
	"m_inputTextureArray" "element_array"
	[
		"CDmeInputTexture"
		{{
			"m_name" "string" "InputTexture0"
			"m_fileName" "string" "{rel}"
			"m_colorSpace" "string" "srgb"
			"m_typeString" "string" "2D"
		}}
	]
	"m_outputTypeString" "string" "2D"
	"m_outputFormat" "string" "RGBA8888"
	"m_textureOutputChannelArray" "element_array"
	[
		"CDmeTextureOutputChannel"
		{{
			"m_inputTextureArray" "string_array" [ "InputTexture0" ]
			"m_srcChannels" "string" "rgba"
			"m_dstChannels" "string" "rgba"
			"m_mipAlgorithm" "CDmeImageProcessor"
			{{
				"m_algorithm" "string" "None"
				"m_stringArg" "string" ""
				"m_vFloat4Arg" "vector4" "0 0 0 0"
			}}
			"m_outputColorSpace" "string" "srgb"
		}}
	]
	"m_bNoLod" "bool" "1"
}}
'''


def load_bundle(bundle_dir):
    config_path = os.path.join(bundle_dir, "bundle.json")
    config = {}
    if os.path.exists(config_path):
        with open(config_path, encoding="utf-8") as fh:
            config = json.load(fh)

    name = config.get("name") or os.path.basename(os.path.normpath(bundle_dir))
    if not name.isidentifier():
        raise SystemExit(f"bundle name '{name}' must be a plain identifier (it becomes a folder name)")
    return name, config


def stage_sources(bundle_dir, config, content_dir):
    """Copies the bundle's panorama/ tree plus any bundle.json copy mappings
    into the CSDK content dir. Returns the staged files as content-relative
    paths like panorama\\layout\\hud_health.xml."""
    if os.path.isdir(content_dir):
        shutil.rmtree(content_dir)

    staged = []
    source_root = os.path.join(bundle_dir, "panorama")
    if not os.path.isdir(source_root):
        raise SystemExit(f"{bundle_dir} has no panorama/ directory - nothing to build")

    for root, _, files in os.walk(source_root):
        for filename in files:
            if ".test." in filename:
                continue   # bun test files live beside the sources but never ship
            full = os.path.join(root, filename)
            rel = os.path.join("panorama", os.path.relpath(full, source_root))
            dst = os.path.join(content_dir, os.path.relpath(rel, "panorama"))
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            shutil.copyfile(full, dst)

            # Images can't be compiled directly: copy the source (the compiler
            # reads it) but stage a generated .vtex definition instead, so the
            # modder just drops a .png and references it as
            # s2r://panorama/images/<name>.vtex - no hand-authoring.
            if os.path.splitext(filename)[1].lower() in IMAGE_EXTS:
                vtex_rel = os.path.splitext(rel)[0] + ".vtex"
                vtex_dst = os.path.splitext(dst)[0] + ".vtex"
                png_rel = rel.replace("\\", "/")
                with open(vtex_dst, "w", encoding="utf-8", newline="\n") as fh:
                    fh.write(VTEX_TEMPLATE.format(rel=png_rel))
                staged.append(vtex_rel)
            else:
                staged.append(rel)

    for mapping in config.get("copy", []):
        src = os.path.normpath(os.path.join(bundle_dir, mapping["from"]))
        rel = os.path.normpath(mapping["to"])
        if not rel.startswith("panorama"):
            raise SystemExit(f"copy target '{mapping['to']}' must live under panorama/")
        dst = os.path.join(content_dir, os.path.relpath(rel, "panorama"))
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copyfile(src, dst)
        staged.append(rel)

    return staged


def compile_order_key(rel_path):
    parts = rel_path.split(os.sep)
    kind = parts[1] if len(parts) > 1 else ""
    return COMPILE_ORDER.index(kind) if kind in COMPILE_ORDER else len(COMPILE_ORDER)


def compile_sources(compiler, game_dir, content_dir, staged):
    for rel in sorted(staged, key=compile_order_key):
        src = os.path.join(content_dir, os.path.relpath(rel, "panorama"))
        print(f"compiling {rel}")
        result = subprocess.run(
            [compiler, "-nop4", "-f", "-game", game_dir, "-i", src],
            capture_output=True, text=True)
        combined = result.stdout + result.stderr
        if result.returncode != 0 or "Compile failed" in combined or "ERROR" in combined:
            print(combined)
            raise SystemExit(f"resourcecompiler failed on {rel}")


def pack_vpk(compiled_dir, expected_count, testing_root):
    sys.path.insert(0, testing_root)
    try:
        from repack_vpk import build_vpk_tree, write_vpk  # noqa: E402
    except ImportError:
        raise SystemExit(f"repack_vpk.py not found in {testing_root} (pass --testing-root)")

    entries = []
    for root, _, files in os.walk(compiled_dir):
        for filename in files:
            full = os.path.join(root, filename)
            rel = os.path.relpath(full, os.path.dirname(compiled_dir))
            path = rel.replace("\\", "/")
            with open(full, "rb") as fh:
                data = fh.read()
            entries.append((path, zlib.crc32(data) & 0xFFFFFFFF, data))
            print(f"packing  {path} ({len(data):,} B)")

    if len(entries) != expected_count:
        raise SystemExit(
            f"expected {expected_count} compiled files, found {len(entries)} - "
            "stale content in the CSDK game tree?")

    return build_vpk_tree(entries), entries


def cache_keys_for(staged):
    """One cache key per layout: the source path with backslashes, which is
    how Panorama keys its cache (never the compiled .vxml_c name)."""
    return [rel for rel in staged
            if rel.split(os.sep)[1:2] == ["layout"] and rel.endswith(".xml")]


def write_csharp_const(path, name, url, sha256, cache_keys):
    """Regenerates the SDK's baked-in default for this bundle (the
    UiHostBundle pattern), so 'rebuild the bundle' and 'update the SDK's
    pinned hash' are one step instead of a copy-paste."""
    keys = ", ".join('@"' + k + '"' for k in cache_keys)
    class_name = "Ui" + name[2:].capitalize() + "Bundle" if name.startswith("dw") else "Ui" + name.capitalize() + "Bundle"
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(f'''namespace DeadworksManaged.Api;

/// <summary>
/// The SDK's pinned default for the <c>{name}</c> bundle - url and SHA-256 of
/// the officially published build, so plugins work with no bundle config at
/// all. GENERATED by examples/ui/build_bundle.py; do not edit by hand -
/// rebuild the bundle instead (its bundle.json points here).
/// </summary>
public static class {class_name}
{{
	/// <summary>The app id panels announce with.</summary>
	public const string Id = "{name}";

	/// <summary>Where the officially published build is served from.</summary>
	public const string Url = "{url}";

	/// <summary>SHA-256 of that build; clients refuse anything else.</summary>
	public const string Sha256 = "{sha256}";

	/// <summary>A ready-to-publish bundle pinned to the official build.</summary>
	public static UiBundle Create() => new()
	{{
		Id = Id,
		Url = Url,
		Sha256 = Sha256,
		CacheKeys = [{keys}],
	}};
}}
''')


def write_manifest(path, name, url, sha256, cache_keys):
    """The machine-readable twin of the printed snippet. UiBundle.FromManifest
    reads this file, so shipping a rebuild is 'copy the manifest next to the
    plugin' instead of re-pasting url/hash/keys into a config."""
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        json.dump({"id": name, "url": url, "sha256": sha256,
                   "cacheKeys": cache_keys}, fh, indent=2)
        fh.write("\n")


def main():
    parser = argparse.ArgumentParser(description="Build a Deadworks UI bundle VPK.")
    parser.add_argument("bundle_dir", help="directory containing bundle.json and panorama/")
    parser.add_argument("--csdk", default=DEFAULT_CSDK, help="CSDK root (Reduced_CSDK_12)")
    parser.add_argument("--testing-root", default=DEFAULT_TESTING_ROOT,
                        help="directory containing repack_vpk.py")
    parser.add_argument("--url-prefix", default=None,
                        help="base URL for the printed config snippet")
    parser.add_argument("--out", default=None, help="output directory for the VPK")
    parser.add_argument("--write-csharp", default=None, metavar="PATH",
                        help="regenerate a C# constants file (UiHostBundle-style) with the new "
                             "url + sha256, so the SDK's built-in default tracks the bundle. "
                             "bundle.json's \"csharpConst\" sets a default path.")
    args = parser.parse_args()

    bundle_dir = os.path.abspath(args.bundle_dir)
    name, config = load_bundle(bundle_dir)

    compiler = os.path.join(args.csdk, r"game\bin_cs2\win64\resourcecompiler.exe")
    game_dir = os.path.join(args.csdk, r"game\citadel")
    content_dir = os.path.join(args.csdk, "content", "citadel_addons", name, "panorama")
    compiled_dir = os.path.join(args.csdk, "game", "citadel_addons", name, "panorama")
    if not os.path.exists(compiler):
        raise SystemExit(f"resourcecompiler not found: {compiler} (pass --csdk)")

    out_dir = args.out if args.out else config.get("outDir", "dist")
    if not os.path.isabs(out_dir):
        out_dir = os.path.normpath(os.path.join(bundle_dir, out_dir))
    url_prefix = args.url_prefix or config.get("urlPrefix", DEFAULT_URL_PREFIX)

    if os.path.isdir(compiled_dir):
        shutil.rmtree(compiled_dir)

    staged = stage_sources(bundle_dir, config, content_dir)
    compile_sources(compiler, game_dir, content_dir, staged)
    (tree, data), _ = pack_vpk(compiled_dir, len(staged), args.testing_root)

    from repack_vpk import write_vpk  # already imported by pack_vpk
    os.makedirs(out_dir, exist_ok=True)
    tmp = os.path.join(out_dir, f"{name}_building.vpk")
    write_vpk(tmp, tree, data)

    with open(tmp, "rb") as fh:
        sha256 = hashlib.sha256(fh.read()).hexdigest()

    # Content-addressed name: the CDN can cache it forever and a new build
    # never collides with what a client already downloaded.
    final = os.path.join(out_dir, f"{name}_{sha256[:8]}.vpk")
    if os.path.exists(final):
        os.remove(final)
    os.rename(tmp, final)

    keys = cache_keys_for(staged)
    keys_json = ", ".join('"' + k.replace("\\", "\\\\") + '"' for k in keys)

    const_path = args.write_csharp or config.get("csharpConst")
    if const_path:
        if not os.path.isabs(const_path):
            const_path = os.path.normpath(os.path.join(bundle_dir, const_path))
        write_csharp_const(const_path, name, url_prefix + os.path.basename(final), sha256, keys)
        print(f"updated : {const_path}")

    manifest = os.path.join(out_dir, f"{name}.manifest.json")
    write_manifest(manifest, name, url_prefix + os.path.basename(final), sha256, keys)

    print(f"\nbundle  : {final}")
    print(f"sha256  : {sha256}")
    print(f"manifest: {manifest}")
    print(f"""
Load it in a plugin with (no values to paste):

  Bundle = UiBundle.FromManifest(Path.Combine(this.GetDataDirectory(), "{name}.manifest.json"))

after copying the manifest into the plugin's data folder
(managed\\plugins\\<Name>\\ next to the DLL). Or paste by hand:

  "Url": "{url_prefix}{os.path.basename(final)}",
  "Sha256": "{sha256}"
  // CacheKeys config only needed if not [{keys_json}]

Upload {os.path.basename(final)} to your HTTPS host ({url_prefix}...).

Remember: if you changed a .js since the last publish, its FILENAME must have
changed too (dwscore1 -> dwscore2), or clients keep running the old compiled
script. Layout-only changes reload fine under the same name.""")


if __name__ == "__main__":
    main()
