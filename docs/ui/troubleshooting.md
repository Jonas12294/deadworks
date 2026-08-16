# Troubleshooting

Every entry here was learned by losing real hours to it. Find your symptom;
the fixes are ordered by how often they turn out to be the cause.

## First, the checklist that solves most "nothing happens"

1. **Are you in a match?** Not the main menu. `hud_health` (and most HUD
   panels) do not exist in the menu, and server messages don't arrive there
   either. A quiet main menu proves nothing.
2. **Did you launch with `-console -condebug`?** Without them there is no
   console and no `console.log`, so everything *looks* dead.
3. **Is the module actually loaded?** Check `%TEMP%\uiwatch.log` (launcher
   inject) or the log next to `uiwatch.dll` (manual inject). No log file = no
   module. The launcher setting is **off by default**.
4. **Did the player opt in?** The launcher's custom-UI toggle gates
   everything.
5. **Was the panel already on screen before content mounted?** Mounted
   content does not apply to panels Panorama cached at startup. Publishing
   with the right `CacheKeys` forces the reload; without them, the panel has
   to reload for some other reason first.

## Symptom → cause → fix

### "I edited my script and nothing changed"

The engine **never invalidates a compiled script**. The panel reloads, the
VPK contains your new bytes, and the old compiled script keeps running. This
is the most deceptive failure in the whole system.

**Fix:** rename the script (`myui1.js` → `myui2.js`) and update the layout's
`<scripts>` include. A fresh path has no cache entry.

**Tell-tale:** two consecutive `$.Msg` calls where only the first prints —
the running revision only had the first.

### "My bundle published but the panel looks stock"

- `CacheKeys` wrong or missing. The key must be the **source** path with
  **backslashes**: `panorama\layout\hud_health.xml`. Forward slashes are
  rejected at publish time; a compiled name (`.vxml_c`) silently matches
  nothing.
- The panel isn't on screen yet (main menu — see checklist).
- The download or hash check failed — check the module log; a hash mismatch
  is logged and the bundle refused (that's the `!ui corrupt` test working as
  designed, but on your bundle it means the config hash doesn't match the
  uploaded file — did you upload the *new* VPK and update *both* URL and
  hash?).

### "I revoked the bundle but the old UI is still on screen"

Expected mechanics: unmounting alone never changes what's rendered — a loaded
panel keeps its cached copy. The client handles this (evict after unmount) for
keys named in the original publish. A panel *not* named in `CacheKeys` reverts
only on its next natural reload.

### "It worked, then I replaced the file on the CDN and nothing updated"

Two possibilities:

- The client had a cached bundle whose hash still matched the manifest — you
  changed the file but not the `Sha256` in the config. Update both.
- You kept the same filename with caching enabled on your host. Use the
  content-addressed names the build tool produces; every build gets a new URL.

### "Loose files in addons_dev don't override anything"

They never can. Source 2 orders **every VPK ahead of every loose directory**;
`pak01.vpk` always wins against a loose file. Content must ship as a VPK —
this is why the build tool exists. (This one silently invalidated hours of
testing once; the reload was working the whole time, re-reading `pak01`.)

### "`!ui` says the variant is misconfigured"

The plugin validates at publish: URL must be `https://`, hash must be 64 hex
chars. The error message names the field.

### "Emit throws ArgumentException"

Line break in a key or value. Newline is the wire separator; strip `\r`/`\n`
from anything player-controlled (names are the classic case) before emitting.

### "My action handler never fires"

- The panel side must send the exact event name you subscribed to.
- With `[UiActionHandler]`, check the server log at plugin load for the
  `Registered UI action handler: <plugin>.<method> -> <event>` line. A missing
  line means the method was skipped — either its signature is not
  `void(UiAction)`/`HookResult(UiAction)` or the event name was refused; the
  reason is logged beside it.
- An earlier handler for the same event may have returned `HookResult.Stop`,
  which owns the action and keeps later handlers from running.
- Payload too big? Inbound rides one console command, ~380 bytes decoded —
  oversized payloads are dropped.
- The action reaches the server as the console command `dw_ui_action`; if a
  match is running and other actions (ping) come through, the channel is fine
  and the payload is the problem — undecodable base64 or a payload missing
  `event=` is dropped silently by design.

### "The panel shows 'helper missing' / red strip"

The `deadworks.js` include failed: wrong include path, wrong order (helper
must load before your script), or the helper file didn't make it into the VPK
(check the build tool's "packing" output lines).

### "The panel loads but shows 'waiting for server' forever / orange strip"

The panel is fine; no emit has arrived. Either the server isn't emitting
(check the plugin loaded and its timer/config), or the module's event
registration failed after a game patch — check the module log for which
template event it borrowed (the `AddStyle` line); after any patch the
client-side offsets in `client/dllmain.cpp` need re-verifying.

### Build problems

- **`resourcecompiler` aborts with schema mismatches** — you're running the
  CSDK's `bin` or `bin_server` copy. Only `game/bin_cs2/win64/resourcecompiler.exe`
  works; the build tool defaults to it.
- **`LNK1104: cannot open uiwatch.dll`** (client module build) — the game
  still has the DLL loaded. Close Deadlock or let the module self-eject.
- **`vswhere.exe is not recognized`** during `build.bat` — harmless noise.
- **`MSB3023` warning** building plugins — expected; auto-deploy is
  deliberately unset. Copy the DLL to
  `<Deadlock>\game\bin\win64\managed\plugins\` by hand.
- **Launcher build fails on a locked exe** — close the running launcher
  first.

## Reading the logs

| Log | Where | What it tells you |
|---|---|---|
| Module log | `%TEMP%\uiwatch.log` (launcher inject) or beside `uiwatch.dll` | Download, hash check, mount, event registration, emit dispatch |
| Panel scripts | `<Deadlock>\game\citadel\console.log`, `[PanoramaScript]` lines | Your `$.Msg` output — needs `-condebug` |
| Reload signal | Deadlock console | `Panel <name> is reloading layout <path>` — unconditional, the definitive "reload happened" |
| Server side | Deadworks server console | Plugin logs, action handler output |

## Still stuck?

`client/dllmain.cpp` carries the evidence behind every client-side rule here —
every signature, vtable index and offset is documented in comments next to the
code that uses it. If a game patch just shipped, start there: each of those
offsets must be re-verified per patch.
