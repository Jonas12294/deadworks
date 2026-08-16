// Deadworks server-data dashboard - the JavaScript half, run natively in the
// panel's V8 context. It subscribes to data the server streams over the channel
// and fills the pre-created rows: the stat tiles, player table and event feed
// all come from pushed data. None of it is possible without the server.
//
// Edit and save: it re-runs live.

(function () {
    "use strict";

    var MAX_ROWS = 8;   // matches prow0..prow7 in ui.xml
    var MAX_EVENTS = 6; // matches ev0..ev5

    // The host runtime hoists the panel to the HUD root, so $("#id") won't
    // resolve into it - search from this context's root instead.
    var ROOT = (function () {
        var p = $.GetContextPanel ? $.GetContextPanel() : null;
        while (p && p.GetParent && p.GetParent()) p = p.GetParent();
        return p;
    })();
    function byId(id) {
        return ROOT && ROOT.FindChildTraverse ? ROOT.FindChildTraverse(id) : null;
    }
    function text(id, value) {
        var p = byId(id);
        if (p) p.text = String(value);
    }
    function show(id, on) {
        var p = byId(id);
        if (p) p.visible = !!on;
    }

    function teamColor(team) {
        var t = parseInt(team, 10);
        if (t === 2) return "#f0b232";   // Amber
        if (t === 3) return "#3fa7f0";   // Sapphire
        return "#cccccc";
    }
    function money(nw) {
        var n = parseInt(nw, 10);
        if (!isFinite(n)) return "0";
        if (n < 1000) return String(n);
        return (Math.floor(n / 100) / 10) + "k";
    }

    if (typeof Deadworks === "undefined") {
        text("conn", "helper missing");
        return;
    }

    // Drop any subscriptions from a previously-loaded UI so switching UIs (or
    // re-running this script) starts clean.
    if (Deadworks.Reset) Deadworks.Reset();

    // --- stat tiles ----------------------------------------------------------
    Deadworks.Subscribe("server.stats", function (d) {
        text("statClock", (d.clock || "0") + "s");
        text("statPlayers", d.players || "0");
        text("statTick", d.tick || "0");
        text("conn", "live");
        var c = byId("conn");
        if (c) c.style.color = "#00ff88";
        // Per-player values: this emit was addressed to us specifically.
        text("you", "you: " + (d.you || "?") + "  ·  slot " + (d.slot || "?") +
                    "  ·  ui '" + (d.ui || "?") + "'");
    });

    // --- player table: fill the pre-created rows -----------------------------
    Deadworks.SubscribeTable("server.players", function (rows) {
        for (var i = 0; i < MAX_ROWS; ++i) {
            var has = i < rows.length;
            show("prow" + i, has);
            if (!has) continue;
            var r = rows[i];
            text("pn" + i, r.name || "?");
            var nameLabel = byId("pn" + i);
            if (nameLabel) nameLabel.style.color = teamColor(r.team);
            text("pk" + i, r.k || "0");
            text("pd" + i, r.d || "0");
            text("pw" + i, money(r.nw));
        }
    });

    // --- event feed: newest first, into the pre-created lines ----------------
    var g = (typeof globalThis !== "undefined") ? globalThis : {};
    if (!g.dwEvents) g.dwEvents = [];

    Deadworks.Subscribe("server.event", function (d) {
        g.dwEvents.unshift("[" + (d.clock || "?") + "s] " + (d.text || ""));
        if (g.dwEvents.length > MAX_EVENTS) g.dwEvents.length = MAX_EVENTS;
        renderEvents();
    });

    function renderEvents() {
        for (var i = 0; i < MAX_EVENTS; ++i)
            text("ev" + i, g.dwEvents[i] || "");
    }
    renderEvents();

    if ($.Msg) $.Msg("[ui.js] server-data dashboard subscribed\n");
})();
