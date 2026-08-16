# Panel API reference — the `Deadworks` JS helper

`examples/ui/deadworks.js` is the panel side of the data channel. Every bundle
ships a copy of it, included **before** your own script.

## Including it

The build tool copies the helper into your bundle under a versioned name
(`dwcore3.js`, mapped in `bundle.json`), and your layout includes the compiled
names in order:

```xml
<scripts>
    <include src="s2r://panorama/scripts/dwcore3.vjs_c" />
    <include src="s2r://panorama/scripts/myui1.vjs_c" />
</scripts>
```

Helper first — your script references the `Deadworks` global it creates.

If the helper loads but the Deadworks client module is not running (player
didn't opt in), `Deadworks` still exists and every call is a safe no-op:
`Subscribe` callbacks simply never fire and `SendToServer` goes nowhere. Your
script should still guard `typeof Deadworks === "undefined"` for the case
where the include itself failed — the starter and scoreboard show the pattern
(they paint the status strip red).

## `Deadworks.Ready(appId)`

> **Only for scripts shipped inside a bundle.** Never call this from JavaScript
> the server **pushes** (`UI.RunScript` / `ServeScriptFile`): the host bundle
> already announces, and a pushed script that announces again creates a loop —
> announce → server pushes state → the push re-runs the script → announce →
> … The server now detects and breaks that after a few rounds and logs a
> warning, but the call is still wrong in pushed JS.

Announces this panel to the server. Call it once when your script loads —
`appId` must match the server's bundle id (`UiBundle.Id` / `AppId`):

```js
Deadworks.Ready("myui");
```

This is what powers the server side's presence tracking and makes its
`StateRequested` fire with your slot, so the current state arrives without
any polling. It runs on every load *and* reload, so state survives both.
(Internally it rides the reserved `_ready` event — the only `_`-prefixed
event a panel should ever send.)

## `Deadworks.Subscribe(eventName, callback)`

Registers interest in one server event. Call once per event, at script load.

```js
Deadworks.Subscribe("scoreboard.update", function (data) {
    // data is a flat object of strings: the d.* fields of the server's Emit
    $("#dw_status").text = "rows: " + data.n;
});
```

- `data` values are **always strings** — `parseInt` / compare as needed, and
  treat malformed values as absent rather than throwing (a newer server may
  send fields your panel predates).
- Callbacks are isolated: one subscriber throwing does not stop the others
  (the error is logged to `$.Msg`).
- There is no unsubscribe — panels live and die with their layout, and the
  subscriber table dies with the script context.

## `Deadworks.SubscribeTable(eventName, callback)`

For table-shaped events (the server's `UiApp.EmitTable`): the callback gets
`(rows, meta)` instead of the flat key soup.

```js
Deadworks.SubscribeTable("scoreboard.update", function (rows, meta) {
    // rows:  [{ name: "Alice", k: "5", ... }, ...]  (already in server order)
    // meta:  { sort: "kills", reason: "sync" }      (every non-row field)
});
```

Row field values are strings; rows past the server's declared count are
dropped; malformed input shrinks the result rather than throwing.

## `Deadworks.Text(id, value)` / `Deadworks.Show(id, visible)`

Guarded one-liners for the two things panel scripts do constantly:

```js
Deadworks.Text("dw_status", "live");   // $("#dw_status").text = ..., if it exists
Deadworks.Show("dw_row3", false);      // toggle visibility, if it exists
```

No-ops when the panel or `$` is missing, so they are safe under `bun test`.

**Caveat:** both resolve ids with `$("#id")`, which looks up in the layout's
own panel context. If your script *reparents* panels (the scoreboard's
overlay hoist does), `$()` stops finding them — keep a reference to the moved
panel and use `FindChildTraverse` on it instead, as `dwscore4.js` does.

## `Deadworks.SendToServer(eventName, data)`

Sends an action to the server, where it surfaces at `[UiActionHandler(eventName)]` with
your player slot attached.

```js
Deadworks.SendToServer("scoreboard.sort", { column: "kills" });
```

- `data` values are stringified; line breaks are stripped (they are the wire
  separator — this is also what stops a panel forging payload fields).
- **Keep it small**: the whole encoded payload rides one console command,
  ~380 bytes decoded. Send verbs and choices, not state dumps.
- There is no reply channel built in — the server answers, if it wants to,
  with a targeted `UI.Emit` back to your slot (see the ping/pong pattern
  below).

## `Deadworks.FreeCursor(on)`

Shows or hides the mouse cursor over the HUD — the stock `hud_free_cursor`
toggle, dispatched exactly the way the in-game shop does it. Nothing in a
panel can be hovered or clicked until the cursor is free; turn it back off
when your interaction ends so the player can aim again.

```js
Deadworks.FreeCursor(true);    // cursor up — buttons now hover and click
Deadworks.FreeCursor(false);   // back to aiming
```

The server can drive the same toggle per player with
`UiApp.SetFreeCursor(slot, on)`.

## `Deadworks.OnHover(panel, over, out)`

Hover callbacks on a panel via the engine's own `onmouseover`/`onmouseout`
panel events — **never poll mouse coordinates for hover**; the events already
exist (`SetPanelEvent` accepts the full mouse set: `onmouseover`,
`onmouseout`, `onmousedown`, `onmouseup`, `onmouseactivate`, verified in
panorama.dll's event-name resolver).

```js
var btn = root.FindChildTraverse("shop_item_3");
Deadworks.OnHover(btn,
    function () { btn.AddClass("hovered"); Deadworks.SendToServer("shop.peek", { id: "3" }); },
    function () { btn.RemoveClass("hovered"); });
```

Returns `false` when the panel is missing or the runtime has no
`SetPanelEvent`. The cursor must be free (`FreeCursor`) before any panel
receives hover events.

## `Deadworks.MousePosition(panel?)`

The cursor position in **panel units**. The engine's `$.MousePosition()`
returns raw window pixels ("the position of the mouse in the parent window" —
the binding applies no scaling), while panel layout runs in UI units that
differ by the panel's actual UI scale; this helper divides that scale out.

```js
var m = Deadworks.MousePosition(myPanel);   // scale of the panel you measure against
// m = { x, y,                  panel units — comparable to actualxoffset etc.
//       windowX, windowY }     the raw window pixels, if you need them
```

Without a `panel` argument the context panel's scale is used; outside a panel
context it returns `null`. Use it for the cases that genuinely need
coordinates (drag, tooltips following the cursor) — for plain hover, use
`OnHover` and let the engine do the work.

## `Deadworks.Container(id)` / `Create(type, parent, id)` / `Root()`

Building panels from `ui.js` with `$.CreatePanel`. These are **installed by the
server** on every panel announcement rather than shipped in the bundle, so they
are available without republishing content (and absent for players without the
client module, like everything else here).

```js
var box = Deadworks.Container("mybox");        // your container, beside the tree
box.style.width = "300px";
box.style.height = "200px";
box.style.backgroundColor = "#000000cc";

var label = Deadworks.Create("Label", box, "mylabel");
label.text = "hello";
```

They exist because two properties of the host make a bare `$.CreatePanel` look
like it silently failed:

- **The host re-parents itself** into `HudCore` at load, so a panel created
  under the window root is a real child of a real panel and is never drawn.
  `Container` parents relative to the host, wherever it ended up.
- **Every tree push deletes the tree container's children**
  (`RemoveAndDeleteChildren`), so panels created inside it disappear on the
  next state push — announce, resync, or any `ui.xml` save. `Container` puts
  yours *beside* the tree instead, where the rebuild does not reach.

| Call | What it gives you |
|---|---|
| `Container(id)` | A `Panel` of your own that survives tree pushes. The same id again replaces it, so a script re-run never stacks duplicates. Pass `false` as the second argument to put it inside the tree container instead. |
| `Create(type, parent, id)` | `$.CreatePanel` with delete-then-create by id and the failure logged rather than thrown. |
| `Root()` | The container the server's tree is built into — read it; don't put your panels there. |

Two things that are style problems, not creation problems: a fresh panel has
**no size** (an empty `Panel` lays out at 0×0 — check `actuallayoutwidth`), and
`Label` needs `.text` while `Button` needs a child `Label` plus `hittest =
true`. Types are Panorama's (`Panel`, `Label`, `Button`, `Image`, `TextEntry`);
the HTML-ish names in `ui.xml` are mapped server-side and mean nothing to
`$.CreatePanel`.

Prefer a server-side tree update (`app.UpdateNode`, or `e.UpdateNode` in a
`[UiActionHandler]`) for anything the server already knows — it survives
rebuilds by definition. Reach for `$.CreatePanel` when the reaction has to be
instant and local.

## Patterns that work

**Status strip.** Give the panel a small coloured box and drive it through
the lifecycle: magenta in the layout (script never ran) → red if the helper is
missing → orange once subscribed (waiting) → cyan on first data. Screenshots
become self-diagnosing.

**Ready on load.** `Deadworks.Ready("<id>")` at script load makes the server
push the current state immediately — which also proves the whole round trip
without any mouse interaction. (Whether the HUD ever grants a cursor for real
clicks is still an open question — design so the panel is useful without
them; a chat-answering ping button is a good visible extra.)

**Static layout, dynamic values.** Pre-create the panels you need in the
layout (rows, slots, labels), then toggle `visible` and set `Label.text` from
the callback. Don't build panel trees in script.

**Pure logic split.** Keep payload→state functions pure and export them
CommonJS-style at the bottom of the script:

```js
if (typeof module !== "undefined" && module.exports) module.exports = MyUi;
```

Panorama has no module system and skips this line; `bun test` in
`examples/ui/` picks it up and tests your logic against real payload fixtures.
`deadworks.test.js` and `scoreboard/dwscore1.test.js` are the models.

## Environment notes (Panorama's V8)

- No `atob`, no `TextDecoder`, no modules — the helper hand-rolls base64 and
  UTF-8; you never deal with encoding.
- `$.Msg("...")` is your print; it lands in `<Deadlock>\game\citadel\console.log`
  as `[PanoramaScript]` lines (only with `-condebug`).
- Multi-byte UTF-8 (player names) survives the wire — the helper decodes it
  properly.
- Write ES5-style JavaScript; the runtime predates most modern niceties.

## The one rule that outranks all others

**A changed script under an old filename keeps running the old code.** The
engine reloads layouts but never invalidates a compiled script, and it fails
in the worst way: silently, looking exactly like your edit had no effect.
Version your script filenames (`myui1.js` → `myui2.js`) and repoint the
layout's include on every script change. The tell-tale symptom: two
consecutive `$.Msg` calls where only the first prints — the running revision
only had the first.
