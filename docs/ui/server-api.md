# Server API reference — `DeadworksManaged.Api.UI`

Two levels. **`UiApp`** (via `UI.CreateApp`, or `UI.CreateHostApp` for
server-built UI) is the recommended one: it owns publishing, panel presence,
state resync, throttling **and its own lifecycle** — the host publishes to
joiners, cleans up disconnects and disposes the app on plugin unload, so a
plugin creates the app and only answers events. Actions coming back from
panels are handled by `[UiActionHandler]` methods, which need no app at all.
The four raw static methods (`UI.PublishBundle` / `RevokeBundle` / `Emit` /
`On`) underneath remain for low-level control. Source:
`managed/DeadworksManaged.Api/UI/`.

Everything here is a no-op for players not running the client module — the
messages ride a usermessage the stock client ignores.

## `UI.CreateApp(this, UiAppOptions)` → `UiApp`

```csharp
_app = UI.CreateApp(this, new UiAppOptions
{
    Bundle = UiBundle.FromManifest(Path.Combine(this.GetDataDirectory(), "myui.manifest.json")),
    ResyncInterval = 5.Seconds(),      // periodic refresh of live panels
});

_app.StateRequested += slot => _app.EmitTable(slot, "myui.update", BuildTable(slot));
```

Panel actions are answered by attributed methods, not registered here — see
[`[UiActionHandler]`](#uiactionhandler--receiving-panel-actions).

**There is no lifecycle code to write.** Creating the app registers it with
the host, which then publishes to joining players (`PublishOnConnect`, on by
default), publishes immediately to anyone already connected (so a plugin
reload mid-match just works), clears presence on disconnect, and disposes the
app when the plugin unloads. `HandleConnect`/`HandleDisconnect` still exist
for manual control and are harmless to call on top — a repeated connect never
double-publishes.

Defaults worth knowing:

- `MinEmitInterval` defaults to **250 ms** — bursts per (player, event)
  coalesce to the latest payload, never dropped. Set `null` to opt out.
- `UiBundle.CacheKeys` defaults to `panorama\layout\hud_health.xml`, the
  proven reload target every shipped bundle patches.
- `UiBundle.FromManifest(path)` loads the `{"id","url","sha256","cacheKeys"}`
  manifest that `build_bundle.py` writes next to the VPK, so a rebuild is one
  file copy instead of re-pasting values. Hand-built `new UiBundle { … }`
  works exactly as before.
- `this.GetDataDirectory()` is the plugin's writable folder
  (`managed\plugins\<Name>\`, beside the plugin DLL). Put manifests, UI
  folders and saved state there; never probe for a folder by hand.

| Member | What it does |
|---|---|
| `StateRequested` (event) | Fires with a slot when that panel announced itself (`Deadworks.Ready`, on load or reload) and on every `ResyncInterval` tick per live panel. Push your full state here — usually the only callback you need. |
| `IsPanelLive(slot)` / `LivePanels` | Presence: which players actually have your panel open. Players without the client module simply never appear. |
| `Emit(slot, ev, data)` / `EmitTable(slot, ev, table)` | Targeted send, throttled if `MinEmitInterval` is set. Bursts coalesce to the latest payload — never dropped. |
| `EmitAllLive(ev, slot => data)` | Per-player send to live panels only. |
| `On(ev, handler)` | Same as `UI.On`, but the handle is cancelled by `Dispose()`. Prefer `[UiActionHandler]` unless you are registering at runtime. The `On(ev, e => …)` overload delivers a `UiAction` bound to this app. |
| `SetFreeCursor(slot, on)` | Shows/hides that player's mouse cursor over the HUD (the stock `hud_free_cursor` toggle the shop uses). Required before panel buttons can be hovered or clicked; panel script can do the same with `Deadworks.FreeCursor(on)`. |
| `Publish(filter)` / `Revoke(filter)` | Manual bundle control (console commands). |

`Bundle` may be omitted for plugins that manage publishing themselves (the
variant-swapping `CustomUiPlugin` does this) — then set `AppId` so presence
still routes, and `PublishOnConnect = false`.

Event names starting with `_` are reserved for the system (the panel-ready
announcement rides `_ready`); `[UiActionHandler]`/`UI.On`/`app.On` reject them.

## `UI.CreateHostApp(this)` — server-built UI with zero config

The entry point for the no-CSDK route: a `UiApp` pinned to the officially
published host bundle (`UiHostBundle` — url + SHA-256 baked into the SDK).
No url, no hash, no cache keys, and the same automatic lifecycle:

```csharp
_app = UI.CreateHostApp(this);
```

Pass a `UiAppOptions` with a `Bundle` to run a custom host build; everything
else about the options behaves like `CreateApp`.

### Several plugins on one panel

**Any number of plugins can each call `CreateHostApp`.** They share the panel
rather than fight over it: the app id defaults to the plugin's name so they
stay distinct, the bundle is published once per player, every app is told when
the panel announces itself, and the trees they build are **composed
server-side** into one push. Two projects can run side by side with no
coordination.

```csharp
// plugin A                                  // plugin B
_app = UI.CreateHostApp(this);               _app = UI.CreateHostApp(this);
_app.SetTree(slot, MyOverlay());             _app.SetTree(slot, MyHudBar());
```

Each app owns only its own part of the panel: `SetTree` replaces that app's
subtree and leaves the rest alone, `ClearTree(slot)` removes it, and disposing
the app (a plugin unloading) takes its UI off screen without touching anyone
else's.

Two things are still shared, because the panel is:

- **Node ids are one namespace**, like ids on a web page — the client's id map
  is flat. Two apps using `msg` would fight over `UpdateNode`, so prefix yours
  (`myproj_msg`). A collision is detected at compose time and logged naming
  both plugins.
- **Screen space.** Nothing positions your subtree for you, so two full-screen
  UIs overlap exactly as they would if one plugin had built both.

`CreateApp` (your own bundle) still allows one live app per id — there the id
*is* the bundle, and two apps publishing different content to the same panel
would genuinely overwrite each other.

### Hover and form input in markup

Markup served through the library (or `SetMarkup`/`SetTree`) supports hover
and input collection with no JavaScript:

```css
/* ui.css */
.buy { background-color: #111; }
.buy:hover { background-color: #333; }          /* pure client-side */
```

```xml
<!-- ui.xml -->
<panel id="form1">
  <input id="coupon" />
  <button class="buy" onclick="shop.buy" submit="form1"
          onmouseover="shop.peek" onmouseout="shop.unpeek">Buy</button>
</panel>
```

- **`:hover` CSS rules** (and/or a `hoverstyle` attribute, which wins last)
  become the node's hover style — swapped in on mouseover, reverted per
  property on mouseout. Give the base style a value for every property the
  hover style touches, or the hovered value sticks.
- **`onmouseover` / `onmouseout` attributes** send server events (like
  `onclick`, with the node's id in `d.id`). Use sparingly — every hover
  crossing is a client→server message.
- **`submit="<panel id>"`** on a button reads every named `<input>` under
  that panel on click and adds each one's text to the action payload under
  its own id (values capped client-side; the whole action rides the
  ~380-byte inbound limit). An input named `id` is skipped — that key is the
  clicked node's id.
- Builder equivalents: `Ui.Button(...).HoverStyle(...)`, `.OnHover(ev)`,
  `.OnHoverOut(ev)`, `.SubmitFrom(panelId)`.

The cursor must be free before anything can hover or click —
`app.SetFreeCursor(slot, true)` / `Deadworks.FreeCursor(true)`.

## `UiApp.ServeUiLibrary(folder, defaultUi)` → `UiLibrary`

The recommended way to serve server-built UI. A library is a folder of named
UIs — one subfolder each, holding `ui.xml` (required) plus optional `ui.css`
and `ui.js` — served **per player**:

```csharp
_library = _app.ServeUiLibrary("uis", defaultUi: "dashboard");
```

Everything around the folder is built in:

- **State**: a panel that announces itself gets its assigned UI (markup +
  CSS as a tree, then the script run natively) — the `defaultUi` until told
  otherwise. Every script run is preceded by an automatic
  `Deadworks.Reset()`, so switching UIs or hot reloading can never stack
  subscriptions — `ui.js` files no longer need to call it themselves.
- **Hot reload**: editing any of a UI's files re-pushes it to exactly the
  players using it, within a second, mid-match. A broken edit is logged and
  panels keep the last good version.
- **Navigation**: a panel button with `onclick="ui.load"` and
  `id="<ui name>"` switches the clicking player; `library.Assign(slot, name)`
  / `library.AssignAll(name)` do it from code (a chat command, say). Names
  arrive from clients and are validated — anything that isn't a plain folder
  name is rejected.
- **Slot hygiene**: a reused slot never inherits the previous player's UI.

| Member | What it does |
|---|---|
| `Available` | UI names on disk right now. |
| `UiFor(slot)` / `DefaultUi` | What a player sees; the fallback for everyone unassigned. |
| `Assign(slot, name)` | Switch one player now; `false` if no such UI. |
| `AssignAll(name)` | Everyone, including later joiners (sets `DefaultUi`). |
| `Cancel()` | Stop serving (also happens on `Dispose`/unload). |

A relative `folder` lives in the plugin's data directory
(`managed\plugins\<Name>\uis\`), which is where server operators drop UI
folders. `TreeUiPlugin` is the working example — its UI code is one
`CreateHostApp` call, one `ServeUiLibrary` call, and its own game logic.

## `UiTable` — rows without the key soup

```csharp
var t = new UiTable { Meta = { ["sort"] = "kills" } };
foreach (var r in rows)
    t.AddRow(new() { ["name"] = r.Name, ["k"] = r.Kills, ["nw"] = r.NetWorth });
_app.EmitTable(slot, "scoreboard.update", t);
```

- Values are any object — stringified with the invariant culture, `null` →
  empty string, **line breaks stripped** (player names cannot break a payload).
- Keys are author code: empty, `=`, or line breaks throw; a meta key that
  collides with the row encoding (`n`, `r0.…`) throws.
- The panel decodes with `Deadworks.SubscribeTable(ev, (rows, meta) => …)` —
  rows arrive as an array of string-valued objects.

## Server-built trees — UI without the CSDK

The host bundle renders trees you describe from the plugin — no layout
compiling, live mid-match. **Setup is automatic**: the SDK pins the
officially published host build (`UiHostBundle.Create()` — url + SHA-256
baked in), so `TreeUiPlugin` works with an empty config. Rebuilding the host
(`build_bundle.py examples/ui/host`) regenerates those constants; upload the
new VPK and rebuild the SDK, done.

```csharp
var tree = Ui.Panel().Style("flow-children: down; horizontal-align: right; margin-top: 340px;")
    .Add(Ui.Label("Hello").Id("msg"))
    .Add(Ui.Button("Ping").OnClick("tree.ping"))     // arrives at [UiActionHandler("tree.ping")]
    .Add(Ui.Image("s2r://panorama/images/….vsvg"));

app.SetTree(slot, tree);                              // full (re)build, chunked
app.UpdateNode(slot, "msg", text: "42");              // cheap in-place change
app.ClearTree(slot);                                  // take just this app's UI away
IHandle h = app.ServeTreeFile("ui.json");             // edit + save = live update
```

- Node types: `Panel`, `Label`, `Button`, `Image`. Styling is inline style
  strings only; the root node positions itself on a full-screen overlay via
  its own align/margin styles.
- `ServeTreeFile` pushes the file's tree to every panel that announces
  itself, polls the file once a second, and re-pushes on change; a parse
  error keeps the last good tree. JSON format mirrors the builder 1:1
  (comments and trailing commas allowed). For most plugins
  `ServeUiLibrary` (above) is the better fit — same hot reload, per player,
  markup + CSS + JS together.
- `SetTree`/`UpdateNode` bypass the emit throttle by design (coalescing
  would drop tree chunks); don't call them on a fast loop — `UpdateNode` is
  the high-frequency path.
- Panels announce the host bundle (`dwhost`), which `CreateHostApp` sets up.
  Several plugins can share it — `SetTree` replaces only your own subtree and
  `ClearTree(slot)` removes it (see
  [Several plugins on one panel](#several-plugins-on-one-panel)).
- Use `TreeUiPlugin` as the template; leave `ResyncInterval` unset with
  the serve helpers (each resync would be a full rebuild).

## Raw JavaScript — `UI.RunScript` / `UiApp.RunScript` / `ServeScriptFile`

Server-authored JavaScript runs **natively in the panel's own V8 context** on
the client — full access to `$` and the panels. Panorama blocks `eval`/`new
Function` from panel script, but the Deadworks client module reaches V8's
embedder path and runs pushed source anyway. Without the module it is a silent
no-op.

```csharp
app.RunScript(slot, "$.GetContextPanel().FindChildTraverse('msg').text = 'hi';");
IHandle h = app.ServeScriptFile("ui.js");   // edit ui.js, save → re-runs live
```

- Runs once per delivery; no return value. It executes *after* the markup for
  that panel, so the panels it manipulates already exist.
- Source is base64-framed on the wire, so any content (quotes, newlines,
  unicode) is safe. Capacity is one usermessage (a few KB of source); split
  larger logic or ship it as bundle content.
- **The host runtime hoists the custom panel to the HUD root**, so `$("#id")`
  won't resolve into it — walk from `$.GetContextPanel()` to the root and
  `FindChildTraverse("id")` instead (see `examples/plugins/TreeUiPlugin/uis/dashboard/ui.js`).
- This is a remote-code channel by construction — the same trust boundary as
  the rest of server-pushed UI. Never run unvalidated third-party source.

With this, all three of raw markup, CSS, and JS push live from the server with
no compiler and no hosting.

The raw static API follows, for when you need it directly.

## `UI.PublishBundle(UiBundle bundle, RecipientFilter recipients)`

Offers a content bundle to the given players. Each client downloads the VPK
over HTTPS, verifies its SHA-256, mounts it ahead of the game's own content,
and reloads the panels named in `CacheKeys` — live, mid-match.

```csharp
UI.PublishBundle(new UiBundle {
    Id        = "scoreboard",
    Url       = "https://cdn.example/ui/scoreboard_3fa1b2c8.vpk",
    Sha256    = "43136268…b8b2d57f",              // 64 lowercase hex chars
    CacheKeys = [@"panorama\layout\hud_health.xml"],
}, RecipientFilter.All);
```

Throws `ArgumentException` when the bundle is malformed — see
[UiBundle](#uibundle) for the exact rules. Publishing the same `Id` again
replaces the previous version on the client (unmount → download if needed →
remount → reload), which is how live updates work.

## `UI.RevokeBundle(string bundleId, RecipientFilter recipients)`

Unmounts a previously published bundle. Panels it replaced revert to the
game's own content on their next reload (the client evicts the cache keys for
you, so panels that were named in the publish revert immediately).

```csharp
UI.RevokeBundle("scoreboard", RecipientFilter.All);
```

## `UI.Emit(string eventName, IReadOnlyDictionary<string, string> data, RecipientFilter recipients)`

Pushes data to panels subscribed to `eventName` via `Deadworks.Subscribe`.

```csharp
UI.Emit("scoreboard.update", new Dictionary<string, string> {
    ["n"]       = "2",
    ["r0.name"] = "Alice",
    ["r0.k"]    = "5",
}, RecipientFilter.Single(slot));
```

Rules and properties:

- **Values are strings.** Numbers, flags — stringify them; the panel parses.
- **No line breaks** in keys or values (`\n`/`\r` throw `ArgumentException`).
  Newline is the wire separator; strip it from anything player-controlled
  (names!) before emitting.
- Keys must not contain `=`; values may.
- **Delivery is fire-and-forget broadcast.** Every subscribed panel on the
  recipient clients receives it; there is no acknowledgement and no "panel
  ready" signal. The robust pattern is a periodic re-emit (the scoreboard
  re-sends its ~1 KB snapshot every 2 s), so a panel that loads late syncs on
  the next tick.
- Capacity is generous in this direction (a 12-player scoreboard snapshot is
  ~1 KB and fine). The raw `UI.Emit` has **no rate limit** — be reasonable;
  every emit dispatches an event on the client's engine thread. `UiApp.Emit`
  throttles by default (250 ms per player and event), which is one of the
  reasons to prefer it.

## `[UiActionHandler]` — receiving panel actions

The way to receive what panels send with `Deadworks.SendToServer`. A method on
your plugin, discovered at load exactly like `[GameEventHandler]`:

```csharp
[UiActionHandler("scoreboard.sort")]
public void OnSort(UiAction e)
{
    string column = e.OneOf("column", "kills", "deaths");   // whitelist, first = default
    e.EmitTable("scoreboard.rows", BuildRows(e.PlayerSlot, column));   // answer the sender
}
```

No registration, no handle to keep, no lambda capturing `_app!` — handlers are
live from load to unload whether or not the plugin ever created a `UiApp`, and
the loader cancels them on unload or reload. One method may carry several
`[UiActionHandler]` attributes.

The method returns `void`, or `HookResult` to decide what later handlers see:

```csharp
[UiActionHandler("shop.buy")]
public HookResult OnBuy(UiAction e)
    => Purchase(e.PlayerSlot, e.Text("item", 32)) ? HookResult.Stop : HookResult.Continue;
```

`HookResult.Stop` (or `Handled`) keeps other handlers for the same event from
running; `Continue` — and every `void` handler — lets them through. A method
with any other signature is skipped with a logged line rather than failing the
plugin load.

### The `UiAction`

| Member | What it gives you |
|---|---|
| `PlayerSlot` / `Player` | Who sent it. **Trustworthy** — taken from the connection the action arrived on (the transport is a server-side console command flagged for remote clients), not from anything the client claims. |
| `Name` | The action name, so one method can serve several. |
| `GetString(k, def)` / `GetInt(k, def)` / `GetFloat(k, def)` / `GetBool(k, def)` | Plain reads with a fallback. |
| `GetInt(k, min, max, fallback)` | Parse **and clamp** — a client can never push a value outside the range. |
| `OneOf(k, ...allowed)` | Whitelist; the first entry is the default, so unknown input cannot leak through. |
| `Text(k, maxLength)` | Trim and hard-cap free text. |
| `Has(k)` / `Data` | Presence check, and the raw dictionary for anything else. |

And answering the panel that sent it, without touching `_app`:

| Member | What it does |
|---|---|
| `Emit(ev, data)` / `EmitTable(ev, table)` | Push back to this player's panel, throttled by the app like any other emit. |
| `UpdateNode(id, text:, style:, visible:)` | Change one node of this player's tree in place. |
| `SetTree(root)` / `SetMarkup(xml, css)` | Replace this player's panel. |
| `RunScript(js)` / `SetFreeCursor(on)` | Run JS in their panel / free their cursor. These need no app. |
| `App` | The `UiApp` the above resolve to, or null. |

`App` is **the app whose panel is live for this player** — the panel the action
came from — falling back to the plugin's only app when it owns exactly one.
That covers every normal plugin. If a plugin owns several apps and none of
their panels is live for that player, `App` is null and the methods above log
which call could not be resolved and do nothing (a handler must never take the
dispatcher down). Two ways to stay exact when that matters: call the app you
want directly (`_app.Emit(e.PlayerSlot, …)`), or register through
`app.On(ev, e => …)`, which binds that app with no resolution at all.

**Everything except the slot is untrusted.** A player can send any event name
with any fields regardless of what your UI offers — validate exactly as you
would chat input, which is what the validating readers above are for.

Other properties:

- **Inbound payloads are capped at ~380 bytes decoded** (console-command
  length). Design actions as small verbs, not data uploads.
- Handler exceptions are caught and logged; one bad handler does not break
  the others.

## `UI.On(string eventName, Action<int, IReadOnlyDictionary<string, string>> handler)`

The runtime equivalent, for handlers whose event name is not known until the
plugin runs (`UiApp.On` is the same thing with the handle cancelled by
`Dispose()`). Returns an `IHandle`; keep it and `Cancel()` it in `OnUnload`.
The same trust rules apply, and the `UiActionData` extensions give the
dictionary the same validating reads:

```csharp
_handles.Add(UI.On("scoreboard.sort", (slot, data) => {
    string column = data.OneOf("column", "kills", "deaths");   // whitelist, first = default
    int page      = data.Int("page", 0, 9, fallback: 0);       // parse + clamp
    bool loud     = data.Flag("loud");                          // 1/true/0/false
    string note   = data.Text("note", maxLength: 64);           // trim + cap
    ...
}));
```

## `UiBundle`

| Field | Required | Rules |
|---|---|---|
| `Id` | yes | Stable identifier. Re-publishing the same id replaces the old version. |
| `Url` | yes | Must start with `https://`. |
| `Sha256` | yes | 64 hex chars, the hash of the VPK. Clients verify and refuse mismatches; a cached copy with a matching hash skips the download. |
| `CacheKeys` | no | Panels to reload after mounting. **Source path with backslashes**: `panorama\layout\hud_health.xml` — never forward slashes (rejected), never the compiled `.vxml_c` name. **Defaults to `hud_health.xml`**, the proven reload target; set `[]` explicitly to mount without reloading anything (content then applies on each panel's next natural load). |

The bundle **must be a VPK** — Source 2 orders every VPK ahead of every loose
directory, so loose files can never override packed game content. Build with
`examples/ui/build_bundle.py`, which also prints the correct cache keys and
hash.

## Wire format (for the curious)

Both directions carry `UiPayload`: line-oriented `key=value` UTF-8, first line
`v=1`, `d.`-prefixed keys for user data. Server→client rides usermessage 148
(`CUserMsg_CustomGameEvent`, events `dw.ui.bundle` / `dw.ui.revoke` /
`dw.ui.emit`); panel→server rides the `dw_ui_action` console command with the
payload base64-encoded. The version line makes both ends fail closed across
version skew. You never touch any of this directly — `UI.*` and the JS helper
are the whole contract — but it explains the no-line-breaks rule and the
inbound size cap.
