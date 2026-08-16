# Interactive live scoreboard (legacy demo)

> **Legacy.** The scoreboard's server-side logic lived in `CustomUiPlugin`,
> which has since been stripped down to a transport-test harness — the
> variant swap (`!ui scoreboard`) still delivers this bundle and `!ui emit`
> still exercises the data channel, but the sort/ping handlers are gone.
> For a live, working demo of everything this showed, use
> `examples/plugins/TreeUiPlugin/` (no CSDK needed); for a bundle-route
> template, use `examples/ui/starter/` + `StarterUiPlugin`. The text below
> describes the demo as it was.

The full-featured demo of the server-pushed UI data channel: a panel that
renders live per-player stats and talks back to the server. The UiApp/UiTable
layer it rides is documented in `docs/ui/server-api.md`.

Driven by `examples/plugins/CustomUiPlugin`, built on `UI.CreateApp`: the
panel announces itself with `Deadworks.Ready("scoreboard")`, the plugin's
`StateRequested` answers with a `UiTable` snapshot, and the app's resync
interval keeps live panels current (plus `player_death`, throttled). The
panel sends `scoreboard.sort` (clicking a column header; the server holds a
per-player sort preference and answers with a re-sorted snapshot for that
player only) and `scoreboard.ping` (button — the server replies in chat plus
a `scoreboard.pong` emit the panel displays).

For a minimal starting point, use `examples/ui/starter/` instead — this demo
exists to exercise everything at once.

## Files

| File | Role |
|---|---|
| `bundle.json` | Build settings for `../build_bundle.py` (name, helper mapping, output dir) |
| `panorama/layout/hud_health.xml` | The proven reload-target panel, hosting the `#DwScoreboard` block |
| `panorama/scripts/dwscore4.js` | Rendering + actions + the overlay hoist; pure logic exported for tests |
| `panorama/scripts/dwscore4.test.js` | `bun test` coverage, incl. the full wire-format round trip |

The bundle ships `examples/ui/deadworks.js` as `dwcore3.js` (mapped in
`bundle.json`). Fresh filenames are deliberate: the engine never invalidates
a compiled script, so **every script change must bump the filename**
(`dwscore5.js`, …) and the layout's `<scripts>` include. The layout itself
reloads fine under one name.

Two hard-won details live in `dwscore4.js` and must survive future edits:

- **The overlay hoist.** `hud_health` is only the loader; the script reparents
  `#DwScoreboard` to the HUD root to escape the health cluster's perspective
  transform and clipping.
- **The captured-panel `find()`.** After the hoist, `$("#id")` stops resolving
  into the panel — lookups must go through the captured panel object. This is
  also why this script does NOT use the global `Deadworks.Text`/`Show`
  helpers for its own content.

## Build and publish

```
cd examples/ui && bun test          # pure-logic tests
python build_bundle.py scoreboard   # compile + pack; prints sha256 and config
```

The VPK lands content-addressed in `dist/` next to the bundle (override with
`bundle.json`'s `outDir`). Upload it to any HTTPS host and paste the printed
snippet into the CustomUiPlugin config on the game server.

## Verifying live

1. Start the Deadworks server with the updated `CustomUiPlugin.dll` + config,
   launch Deadlock with `-console -condebug`, module injected, join a match
   (`hud_health` only exists in a match).
2. **Strip orange + "waiting for server"** → bundle mounted, scripts ran.
3. **Rows fill in and tick** → the panel's `Ready` announcement reached the
   server and `StateRequested` answered — both directions work.
4. Click a column header → rows reorder (watch the server console log the
   sort). The PING button proves the chat + pong path.
5. `!ui emit` reports how many live panels it pushed to — presence working.

Status strip: magenta = script didn't run, red = helper include missing,
orange = waiting for first emit, cyan = live data arrived.
