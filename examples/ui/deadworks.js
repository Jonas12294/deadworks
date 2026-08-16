// Deadworks server UI bridge for Panorama panels.
//
// Include from a panel layout that a server-published bundle delivers:
//
//   <scripts><include src="file://{resources}/scripts/deadworks.js" /></scripts>
//
// Then:
//
//   Deadworks.Subscribe("scoreboard.update", function (data) { ... });
//
// The server pushes with UI.Emit(...); the payload arrives as a single string
// argument on a Panorama event and is demultiplexed here by its event name.

var Deadworks = (function () {
    "use strict";

    // Bumped in lockstep with UiPayload.Version if the format ever changes shape.
    var PAYLOAD_VERSION = "1";

    var B64_CHARS = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    // Panorama's V8 has no atob() and no TextDecoder, so both halves are done by
    // hand. Payloads carry player names, so multi-byte UTF-8 has to survive.
    function utf8Decode(bytes) {
        var out = "";
        for (var i = 0; i < bytes.length; ) {
            var b = bytes[i++];
            var cp;
            if (b < 0x80) {
                cp = b;
            } else if ((b & 0xE0) === 0xC0) {
                cp = ((b & 0x1F) << 6) | (bytes[i++] & 0x3F);
            } else if ((b & 0xF0) === 0xE0) {
                cp = ((b & 0x0F) << 12) | ((bytes[i++] & 0x3F) << 6) | (bytes[i++] & 0x3F);
            } else {
                cp = ((b & 0x07) << 18) | ((bytes[i++] & 0x3F) << 12) |
                     ((bytes[i++] & 0x3F) << 6) | (bytes[i++] & 0x3F);
            }
            if (cp > 0xFFFF) {
                cp -= 0x10000;
                out += String.fromCharCode(0xD800 + (cp >> 10), 0xDC00 + (cp & 0x3FF));
            } else {
                out += String.fromCharCode(cp);
            }
        }
        return out;
    }

    // Returns null on anything outside the base64 alphabet, so a stray dispatch
    // of the event we ride on is ignored rather than half-parsed.
    function decodeBase64(text) {
        var s = String(text);
        var bytes = [];
        var acc = 0;
        var bits = 0;
        for (var i = 0; i < s.length; ++i) {
            var c = s.charAt(i);
            if (c === "=") break;
            var v = B64_CHARS.indexOf(c);
            if (v < 0) return null;
            acc = (acc << 6) | v;
            bits += 6;
            if (bits >= 8) {
                bits -= 8;
                bytes.push((acc >> bits) & 0xFF);
                acc &= (1 << bits) - 1;   // keep acc from overflowing 32 bits
            }
        }
        return utf8Decode(bytes);
    }

    // Line-oriented key=value, split on the FIRST '=' - the exact format the
    // server writes in UiPayload.cs. Values may contain further '=' characters.
    //
    // Returns null when the leading version line is missing or unrecognised, so
    // a panel talking to a newer server ignores the payload rather than
    // misreading its fields.
    function parsePayload(text) {
        var entries = [];
        var lines = String(text).split("\n");
        for (var i = 0; i < lines.length; ++i) {
            var line = lines[i];
            if (line.charAt(line.length - 1) === "\r") line = line.slice(0, -1);
            if (!line.length) continue;
            var eq = line.indexOf("=");
            if (eq <= 0) continue;
            entries.push([line.slice(0, eq), line.slice(eq + 1)]);
        }

        if (!entries.length || entries[0][0] !== "v" || entries[0][1] !== PAYLOAD_VERSION)
            return null;

        var out = { event: "", data: {} };
        for (var j = 1; j < entries.length; ++j) {
            var key = entries[j][0];
            if (key === "event") out.event = entries[j][1];
            else if (key.slice(0, 2) === "d.") out.data[key.slice(2)] = entries[j][1];
        }
        return out;
    }

    function utf8Encode(str) {
        var bytes = [];
        for (var i = 0; i < str.length; ++i) {
            var cp = str.charCodeAt(i);
            if (cp >= 0xD800 && cp <= 0xDBFF && i + 1 < str.length) {
                var lo = str.charCodeAt(i + 1);
                if (lo >= 0xDC00 && lo <= 0xDFFF) {
                    cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
                    ++i;
                }
            }
            if (cp < 0x80) {
                bytes.push(cp);
            } else if (cp < 0x800) {
                bytes.push(0xC0 | (cp >> 6), 0x80 | (cp & 0x3F));
            } else if (cp < 0x10000) {
                bytes.push(0xE0 | (cp >> 12), 0x80 | ((cp >> 6) & 0x3F), 0x80 | (cp & 0x3F));
            } else {
                bytes.push(0xF0 | (cp >> 18), 0x80 | ((cp >> 12) & 0x3F),
                           0x80 | ((cp >> 6) & 0x3F), 0x80 | (cp & 0x3F));
            }
        }
        return bytes;
    }

    function encodeBase64(bytes) {
        var out = "";
        for (var i = 0; i < bytes.length; i += 3) {
            var b0 = bytes[i];
            var b1 = bytes[i + 1];
            var b2 = bytes[i + 2];
            out += B64_CHARS.charAt(b0 >> 2);
            out += B64_CHARS.charAt(((b0 & 0x03) << 4) | (b1 === undefined ? 0 : b1 >> 4));
            out += b1 === undefined
                ? "="
                : B64_CHARS.charAt(((b1 & 0x0F) << 2) | (b2 === undefined ? 0 : b2 >> 6));
            out += b2 === undefined ? "=" : B64_CHARS.charAt(b2 & 0x3F);
        }
        return out;
    }

    // A line break in a value would open a new payload line, letting a panel
    // forge fields the server never offered. The separator simply cannot appear
    // in the data.
    function stripBreaks(value) {
        return String(value).replace(/[\r\n]/g, "");
    }

    // Builds a payload the server's UiPayload.Parse accepts, base64 for the trip
    // through the console command.
    function encodePayload(eventName, data) {
        var text = "v=" + PAYLOAD_VERSION + "\nevent=" + stripBreaks(eventName) + "\n";
        if (data) {
            for (var key in data) {
                if (!Object.prototype.hasOwnProperty.call(data, key)) continue;
                text += "d." + stripBreaks(key) + "=" + stripBreaks(data[key]) + "\n";
            }
        }
        return encodeBase64(utf8Encode(text));
    }

    // Event name -> callbacks. Created without a prototype because the key comes
    // off the wire; a server naming its event "__proto__" would otherwise be
    // writing to Object.prototype.
    var subscribers = Object.create(null);

    function log(message) {
        if (typeof $ !== "undefined" && $.Msg) $.Msg("[deadworks] " + message + "\n");
    }

    // Registers interest in one server event. Call it once per panel, at load.
    function Subscribe(eventName, callback) {
        if (!eventName || typeof callback !== "function") return;
        if (!subscribers[eventName]) subscribers[eventName] = [];
        subscribers[eventName].push(callback);
    }

    // Drops app subscriptions so switching or reloading a UI starts clean
    // instead of stacking stale callbacks (Subscribe only ever appends).
    // System events (the "dw." namespace - e.g. the host runtime's own
    // "dw.tree.set" / "dw.tree.update" renderer) are KEPT: clearing those would
    // unsubscribe the tree renderer and freeze the panel.
    function Reset() {
        var kept = Object.create(null);
        for (var name in subscribers)
            if (name.slice(0, 3) === "dw.")
                kept[name] = subscribers[name];
        subscribers = kept;
    }

    // Entry point for one dispatch of the shared Panorama event.
    //
    // The engine decides what a handler receives, and for an unhandled event it
    // passes the panel the event was dispatched at before the event's own
    // arguments - so the payload is not reliably the first parameter. Rather
    // than depend on that order, take the first argument that decodes into a
    // payload we recognise.
    //
    // Everything else is dropped silently: this rides a real event type, so it
    // has to tolerate arguments that were never ours.
    function handleRaw() {
        for (var i = 0; i < arguments.length; ++i) {
            var candidate = arguments[i];
            if (typeof candidate !== "string") continue;

            var text = decodeBase64(candidate);
            if (text === null) continue;

            var payload = parsePayload(text);
            if (!payload || !payload.event) continue;

            deliver(payload);
            return;
        }
    }

    function deliver(payload) {
        var list = subscribers[payload.event];
        if (!list) return;

        for (var i = 0; i < list.length; ++i) {
            // One bad panel callback must not stop the rest from updating.
            try {
                list[i](payload.data);
            } catch (err) {
                log("subscriber for '" + payload.event + "' threw: " + err);
            }
        }
    }

    // The Panorama event the Deadworks client module registers and dispatches on.
    // Every server emit rides this one event; the payload says which.
    var EVENT_NAME = "DWData";

    // Client command the module registers; the stock CitadelConCommand binding
    // is what carries the action out of the panel.
    var ACTION_COMMAND = "dw_ui_action";

    // Sends an action back to the server, where it surfaces as UI.On(eventName).
    // Values are strings; anything else is stringified.
    function SendToServer(eventName, data) {
        if (typeof $ === "undefined" || !$.DispatchEvent) return;
        $.DispatchEvent("CitadelConCommand",
                        ACTION_COMMAND + " " + encodePayload(eventName, data));
    }

    // Announces this panel to the server's UiApp for the given bundle id.
    // Call once when your script loads: it marks the player's panel as live,
    // and the server answers by pushing the current state (StateRequested).
    // '_'-prefixed events are reserved for the system; this is the only one
    // a panel sends.
    function Ready(appId) {
        SendToServer("_ready", { app: appId });
    }

    // Rebuilds rows and table-level values from the flat encoding the server's
    // UiTable writes: n=<count>, r<i>.<field>=<value>, everything else meta.
    // Row indices at or past the declared count are dropped; declared rows
    // with no fields are skipped. Malformed input shrinks the result rather
    // than throwing.
    function tableFromData(data) {
        var rows = [];
        var meta = {};
        if (!data) return { rows: rows, meta: meta };

        var n = parseInt(data.n, 10);
        if (!isFinite(n) || n < 0) n = 0;

        var byIndex = [];
        for (var key in data) {
            if (!Object.prototype.hasOwnProperty.call(data, key)) continue;
            if (key === "n") continue;
            var m = /^r(\d+)\.(.+)$/.exec(key);
            if (m) {
                var i = parseInt(m[1], 10);
                if (i >= n) continue;
                if (!byIndex[i]) byIndex[i] = {};
                byIndex[i][m[2]] = data[key];
            } else {
                meta[key] = data[key];
            }
        }

        for (var j = 0; j < n; ++j)
            if (byIndex[j]) rows.push(byIndex[j]);

        return { rows: rows, meta: meta };
    }

    // Subscribe for table-shaped events (the server's UiApp.EmitTable): the
    // callback receives (rows, meta) instead of the flat key soup.
    function SubscribeTable(eventName, callback) {
        if (typeof callback !== "function") return;
        Subscribe(eventName, function (data) {
            var t = tableFromData(data);
            callback(t.rows, t.meta);
        });
    }

    // Shows or hides the mouse cursor over the HUD - the stock
    // hud_free_cursor toggle (exactly what the in-game shop dispatches).
    // Buttons only hover and click while the cursor is free; turn it back
    // off when your interaction ends so the player can aim again. The
    // server can drive the same toggle with UiApp.SetFreeCursor(slot, on).
    function FreeCursor(on) {
        if (typeof $ === "undefined" || !$.DispatchEvent) return;
        $.DispatchEvent("CitadelConCommand", "hud_free_cursor " + (on ? "1" : "0"));
    }

    // Mouse position in panel units. $.MousePosition() returns the cursor in
    // raw window pixels ("position of the mouse in the parent window" - the
    // binding applies no scaling), while panel layout runs in UI units that
    // differ by the panel's actual UI scale - so the raw values are divided
    // by actualuiscale before use. Pass the panel you are measuring against;
    // without one the context panel's scale is used. Returns
    // { x, y, windowX, windowY } (window* = the unscaled pixels), or null
    // outside a panel context.
    //
    // For hover detection do NOT poll this every frame - the engine has real
    // hover events; see OnHover below.
    function MousePosition(panel) {
        if (typeof $ === "undefined" || !$.MousePosition) return null;
        var raw = $.MousePosition();
        if (!raw) return null;
        var ctx = panel || ($.GetContextPanel ? $.GetContextPanel() : null);
        var sx = ctx && ctx.actualuiscale_x > 0 ? ctx.actualuiscale_x : 1;
        var sy = ctx && ctx.actualuiscale_y > 0 ? ctx.actualuiscale_y : 1;
        return { x: raw.x / sx, y: raw.y / sy, windowX: raw.x, windowY: raw.y };
    }

    // Hover callbacks on a panel, via the engine's own onmouseover /
    // onmouseout panel events (SetPanelEvent) - no per-frame polling. The
    // cursor must be free (FreeCursor) before any panel gets hover events.
    // Returns false when the panel cannot take events (null, or a runtime
    // where SetPanelEvent is unavailable).
    function OnHover(panel, over, out) {
        if (!panel || typeof panel.SetPanelEvent !== "function") return false;
        if (typeof over === "function") panel.SetPanelEvent("onmouseover", over);
        if (typeof out === "function") panel.SetPanelEvent("onmouseout", out);
        return true;
    }

    // Guarded panel helpers, so rendering code shrinks to its logic and works
    // (as a no-op) under bun test where $ does not exist.
    function Text(id, value) {
        if (typeof $ === "undefined") return;
        var panel = $("#" + id);
        if (panel) panel.text = String(value);
    }

    function Show(id, visible) {
        if (typeof $ === "undefined") return;
        var panel = $("#" + id);
        if (panel) panel.visible = !!visible;
    }

    var registered = false;

    // Nothing arrives until this runs, and it only works inside a panel context.
    function attach() {
        if (registered) return;
        if (typeof $ === "undefined" || !$.RegisterForUnhandledEvent) return;
        try {
            $.RegisterForUnhandledEvent(EVENT_NAME, handleRaw);
            registered = true;
        } catch (err) {
            // Thrown when the event type is not registered, i.e. the Deadworks
            // client module is not loaded. Panels still work, just without data.
            log("could not listen for " + EVENT_NAME + " (" + err +
                ") - server data will not arrive");
        }
    }

    attach();

    return {
        parsePayload: parsePayload,
        decodeBase64: decodeBase64,
        Subscribe: Subscribe,
        SubscribeTable: SubscribeTable,
        Reset: Reset,
        tableFromData: tableFromData,
        handleRaw: handleRaw,
        encodePayload: encodePayload,
        SendToServer: SendToServer,
        Ready: Ready,
        Text: Text,
        Show: Show,
        FreeCursor: FreeCursor,
        MousePosition: MousePosition,
        OnHover: OnHover,
    };
})();

// Lets the pure half run under `bun test`; Panorama has no module system and
// simply skips this.
if (typeof module !== "undefined" && module.exports) module.exports = Deadworks;
