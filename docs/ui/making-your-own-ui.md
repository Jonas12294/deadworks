# Making your own UI

This is the tutorial path for the **bundle route**: start from the starter
template, build a bundle, publish it from a plugin, and iterate on it live.
It assumes you have run the demo once ([Getting started](getting-started.md))
so you know what "working" looks like.

> **Do you need a bundle at all?** If your UI is layout + styles + script,
> the server-built route needs none of this — no CSDK, no compiling, no
> hosting: `UI.CreateHostApp(this)` + `app.ServeUiLibrary(...)` serve plain
> `ui.xml`/`ui.css`/`ui.js` folders with live editing (see
> [Getting started](getting-started.md) and `TreeUiPlugin`). Build a bundle
> when you need what only a VPK can carry: custom images, fonts, compiled
> styles, or replacing a stock panel wholesale.

## What you need beyond the demo

| Thing | Why |
|---|---|
| The CSDK (`Reduced_CSDK_12`) | Contains the resource compiler that turns `.xml`/`.js` into engine formats |
| Python 3 | Runs the bundle build tool |
| Any HTTPS host | Clients download your bundle over HTTPS — a static file server is enough |

The one compiler that works is `game/bin_cs2/win64/resourcecompiler.exe` inside
the CSDK. The `bin` and `bin_server` copies abort with schema mismatches — the
build tool defaults to the right one.

## 1. Copy the starter

```
cp -r examples/ui/starter examples/ui/myui
```

The starter is the smallest thing that exercises the whole system:

```
myui/
├── bundle.json                  build settings: name, helper mapping, url prefix
├── panorama/
│   ├── layout/hud_health.xml    the panel (stock layout + a small custom block)
│   └── scripts/dwstart1.js      your panel logic
└── README.md
```

The matching server side is `examples/plugins/StarterUiPlugin/` — a ~100 line
plugin that publishes the bundle on connect, pushes a greeting + clock every
few seconds, and answers the panel's ping. Copy that too if you want your own
plugin rather than reusing it.

### Why the layout is called `hud_health.xml`

Server-built panel trees don't exist yet, so custom UI lives inside an existing
panel that the game already loads. `hud_health` is the proven reload target:
the starter's layout is the stock health bar layout with one extra block added.
Your UI goes inside that block; the health bar keeps working underneath. Keep
the filename.

## 2. Write your panel

Two files matter:

**The layout** (`panorama/layout/hud_health.xml`) — ordinary Panorama XML.
The custom block in the starter shows the patterns you need: labels you set
from script, a `Button` with `onactivate`, and the status strip convention
(orange = waiting, cyan = live) that makes failures visible at a glance.

Two rules the layout must follow:

- Include the scripts as **compiled** names (`.vjs_c`, not `.js`):
  ```xml
  <scripts>
      <include src="s2r://panorama/scripts/dwcore3.vjs_c" />
      <include src="s2r://panorama/scripts/dwstart1.vjs_c" />
  </scripts>
  ```
  The helper (`dwcore3`) must come **before** your script.
- Keep the layout **static**: pre-create the panels you will need and toggle
  `visible` / set `Label.text` from script. (The scoreboard pre-creates 12 rows
  for exactly this reason.)

**The script** (`panorama/scripts/dwstart1.js`) — plain ES5-era JavaScript
(Panorama's V8 has no modules, no `atob`, no `TextDecoder`; the helper handles
all encoding for you). The whole API surface is:

```js
Deadworks.Ready("myui");   // announce: the server pushes state to this panel

Deadworks.Subscribe("myui.update", function (data) {
    Deadworks.Text("dw_message", data.message);   // guarded $("#id").text = ...
});

Deadworks.SubscribeTable("myui.rows", function (rows, meta) { ... });

Deadworks.SendToServer("myui.clicked", { which: "confirm" });
```

See the [Panel API reference](panel-api.md) for the details and limits.

> **The most expensive rule in this whole system:** the engine **never reloads
> a changed script under the same filename**. Layout edits hot-reload fine;
> script edits silently keep running the *old* code, which looks exactly like
> your change having no effect. So every script carries a revision number in
> its filename (`dwstart1.js`, `dwstart2.js`, …) — when you change a script,
> **bump the filename and the layout's include**. The build tool warns you
> about this; believe it.

## 3. Build the bundle

```
python examples/ui/build_bundle.py examples/ui/myui
```

The tool copies your sources (plus the `deadworks.js` helper, mapped in
`bundle.json`) into the CSDK content tree, compiles them, packs a VPK, and
produces:

- the output file — content-addressed, e.g. `myui_3fa1b2c8.vpk`
- its SHA-256
- **`myui.manifest.json`** — id, url, hash and cache keys in one file, which
  is all a plugin needs (`UiBundle.FromManifest`)

Machine paths (CSDK location etc.) are CLI flags with defaults for this
machine — run with `--help` to see them.

## 4. Host the bundle

Upload the VPK anywhere that serves HTTPS. Requirements:

- **HTTPS is mandatory** — `UI.PublishBundle` rejects plain-http URLs.
- The content-addressed filename means you can (and should) let it be cached
  forever; a new build gets a new name.

Any static HTTPS host works — the file is content-addressed and immutable, so
a plain file server or object store is enough. The demo bundles in this repo
are served from `https://glutensnake.com/ui/<name>`.

## 5. Publish it

Copy `StarterUiPlugin` — the whole loop on `UiApp` is this:

```csharp
_app = UI.CreateApp(this, new UiAppOptions
{
    // The manifest from step 3, dropped into managed\plugins\<Name>\:
    Bundle = UiBundle.FromManifest(Path.Combine(this.GetDataDirectory(), "myui.manifest.json")),
    ResyncInterval = 5.Seconds(),
});

_app.StateRequested += slot => _app.Emit(slot, "myui.update", new Dictionary<string, string> {
    ["message"] = "hello from the server",
});

// Elsewhere on the plugin — actions are answered by attributed methods,
// like [GameEventHandler] answers game events:
[UiActionHandler("myui.clicked")]
public void OnClicked(UiAction e) {
    // e.PlayerSlot is authenticated; every value on it came from a client,
    // so read them with e.OneOf / e.GetInt(min, max) / e.Text
    e.Emit("myui.update", new Dictionary<string, string> { ["message"] = "clicked!" });
}
```

That's the entire plugin. The host owns the lifecycle: the bundle is
published to joiners (and to anyone already connected when the plugin
loads), presence is tracked, `StateRequested` fires whenever a panel needs
data, emit bursts are throttled by default, and the app is disposed on
unload — no forwarding overrides, no timers, no bookkeeping.

The bundle id in the manifest must match your panel's
`Deadworks.Ready("myui")`. Cache keys default to `hud_health.xml`, which is
right for anything built from the starter; the build tool writes the exact
keys for other layouts into the manifest.

See the [Server API reference](server-api.md) for `UiTable` (typed rows),
validation rules, payload limits, and the raw `UI.*` API underneath.

## Custom images

Panorama only loads *compiled* textures, never raw image files (proven the
hard way — a loose `.png` is invisible, and even inside a VPK the panel
rewrites every `src` to a compiled resource). The build tool handles this for
you: drop image files into your bundle's `panorama/images/` folder and
`build_bundle.py` generates the `.vtex` definitions, compiles them, and packs
the textures — no hand-authoring.

```
myui/panorama/images/icon.png        →  s2r://panorama/images/icon.vtex
myui/panorama/images/hud/frame.png   →  s2r://panorama/images/hud/frame.vtex
```

Reference the **compiled** name (`.vtex`, not `.png`) in markup or a tree:

```xml
<img src="s2r://panorama/images/icon.vtex" style="width: 64px; height: 64px;" />
```

Supported inputs: `.png`, `.jpg`, `.tga`, `.psd` (8-bit). Images are packed
into the same bundle VPK as your layout, so they ship and mount together —
nothing extra to host.

**Server-built (host-bundle) UI needs none of that.** Drop a PNG into the
plugin's `images/` folder and reference it by name — the server compiles the
texture, packs it and pushes it over the game connection, so nothing is hosted
anywhere (client module v27+):

```csharp
_app.ServeImages();               // managed\plugins\<Name>\images\
```

```xml
<img src="dw://logo.png" style="width: 64px; height: 64px;" />
```

Editing or adding a PNG reaches connected players in about a second. Names
must be plain (no dots beyond the extension).

Images from every serving plugin are packed together, because the client
mounts one images pack — each plugin's folder becomes its own subfolder, so a
bare `dw://logo.png` always resolves to *your* `logo.png` even if another
plugin ships one too. To point at another plugin's image on purpose, name it:
`dw://<their app id>/logo.png`.

Stock game art (`s2r://panorama/images/…`, browsable in Source 2 Viewer) needs
nothing shipped at all and works everywhere.

## 6. Iterate

The loop that works, in a live match:

1. Edit layout and/or script. **If you touched a script, bump its filename**
   and the layout include.
2. `python examples/ui/build_bundle.py examples/ui/myui` — new VPK, new hash,
   new manifest.
3. Upload the VPK, copy the fresh manifest over the old one, and reload the
   plugin. The client unmounts the old bundle, downloads the new one,
   remounts and reloads the panel — you see the change in seconds, without
   leaving the match.

Things that will eat your time if you forget them (the full list is in
[Troubleshooting](troubleshooting.md)):

- **Test in a match**, never the main menu — the HUD panels don't exist there
  and no server messages arrive.
- A panel that was already on screen when content mounted needs one reload
  before your content appears; publishing with the right `CacheKeys` does that
  for you.
- The emit payload rejects line breaks in keys and values, and player names
  are the one field a player controls — sanitise them (the scoreboard plugin
  shows how).
- Panel→server payloads are capped at roughly **380 bytes decoded** — fine
  for actions ("sort by kills"), wrong for bulk data.

## 7. Design for players who don't have the module

Only players who opted in through the launcher run the client module. For
everyone else, every `UI.*` call is a silent no-op. So:

- The panel's on-load ping pattern (see the starter and scoreboard) tells the
  *server* which players actually have a live panel.
- Never gate game-mode logic on the UI being visible. Custom UI decorates the
  experience; chat and console remain the lowest common denominator.
