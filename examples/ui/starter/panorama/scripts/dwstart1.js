// Deadworks starter panel script - the smallest useful panel.
//
// Subscribes to one server event, announces itself so the server knows this
// panel is live, and sends one action back. Everything rides the Deadworks
// helper (shipped as dwcore1.js), included by the layout BEFORE this file.
//
// The filename carries a revision number because the engine never invalidates
// a compiled script: a changed .js under an old path keeps running the old
// code. Bump the name (dwstart2.js, ...) and the layout's include on every
// script change.

var DwStart = (function () {
    "use strict";

    // Must match the server's UiBundle.Id - it is how the server's UiApp
    // knows which app this panel belongs to.
    var APP_ID = "dwstarter";

    // --- pure helpers, covered by bun test ----------------------------------

    // Formats the fields of a starter.hello payload for display. Malformed
    // input degrades rather than throwing - the payload may come from a newer
    // plugin revision.
    function helloText(data) {
        if (!data) data = {};
        return {
            message: String(data.message || ""),
            clock: "server clock " + (data.time || "?") + "s",
        };
    }

    // --- rendering -----------------------------------------------------------

    function setStrip(colour) {
        if (typeof $ === "undefined") return;
        var strip = $("#dw_status_strip");
        if (strip) strip.style.backgroundColor = colour;
    }

    var updates = 0;

    function onHello(data) {
        var text = helloText(data);
        Deadworks.Text("dw_message", text.message);
        Deadworks.Text("dw_clock", text.clock);
        ++updates;
        Deadworks.Text("dw_status", "live · #" + updates);
        setStrip("#00ffff");
    }

    // --- actions (wired to onactivate in the layout) -------------------------

    function ping() {
        if (typeof Deadworks === "undefined") return;
        Deadworks.Text("dw_clock", "ping sent...");
        Deadworks.SendToServer("starter.ping", {});
    }

    // --- wiring --------------------------------------------------------------

    if (typeof $ !== "undefined") {
        if (typeof Deadworks === "undefined") {
            // The helper include is missing or failed to compile; say so on
            // the panel instead of dying silently.
            $.Msg("[dwstart] FAIL - Deadworks helper did not load\n");
            setStrip("#ff0000");
        } else {
            $.Msg("[dwstart] loaded, announcing\n");
            Deadworks.Text("dw_status", "waiting for server");
            setStrip("#ff8800");

            Deadworks.Subscribe("starter.hello", onHello);

            // Tells the server's UiApp this panel exists; the server answers
            // by pushing the current state (StateRequested fires with our
            // slot). Runs on every load and reload, so state survives both.
            Deadworks.Ready(APP_ID);
        }
    }

    return {
        helloText: helloText,
        onHello: onHello,
        ping: ping,
    };
})();

// Lets the pure half run under `bun test`; Panorama has no module system and
// simply skips this.
if (typeof module !== "undefined" && module.exports) module.exports = DwStart;
