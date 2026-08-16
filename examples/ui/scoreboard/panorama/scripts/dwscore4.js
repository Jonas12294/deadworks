// Deadworks live scoreboard - the panel half of the interactive demo.
//
// Included by the bundle's hud_health.xml after the Deadworks helper
// (dwcore2.js). Renders the rows CustomUiPlugin emits as scoreboard.update
// (a UiTable, decoded by Deadworks.SubscribeTable), and sends two actions
// back: scoreboard.sort when a column header is clicked, scoreboard.ping
// from the ping button. Deadworks.Ready announces the panel, which is what
// makes the server push the first snapshot.
//
// hud_health is only the LOADER: it is the one panel proven to reload from a
// pushed bundle, but everything under it inherits the health cluster's
// perspective transform and clips badly. So at load the script hoists the
// scoreboard panel out: it walks to the HUD root and reparents #DwScoreboard
// there, where the XML's align/margin styles place it as a free-standing
// top-right overlay. If reparenting is unavailable the panel simply stays
// where it loaded - degraded but alive.
//
// The filename carries a revision number because the engine never invalidates
// a compiled script: a changed .js under an old path keeps running the old
// code. Bump the name (dwscore5.js, ...) and the layout's include on every
// script change. (Revision 4 = revision 3's hoist + the UiApp/UiTable layer.)

var DwScore = (function () {
    "use strict";

    // Must match CustomUiPlugin's BundleId config (default "scoreboard").
    var APP_ID = "scoreboard";

    // Matches the 12 dw_row panels in the layout; extra rows are dropped.
    var MAX_ROWS = 12;

    var COLUMNS = ["kills", "deaths", "assists", "networth", "level"];

    // --- pure functions, covered by bun test ---------------------------------

    // 12345 -> "12.3k", 950 -> "950". Keeps the column narrow.
    function formatNetWorth(value) {
        var n = parseInt(value, 10);
        if (!isFinite(n) || n < 0) return "0";
        if (n < 1000) return String(n);
        var k = Math.floor(n / 100) / 10;
        return String(k) + "k";
    }

    // Deadlock teams: 2 = Amber, 3 = Sapphire. Team arrives as a string field.
    function teamColour(team) {
        var t = parseInt(team, 10);
        if (t === 2) return "#f0b232";
        if (t === 3) return "#3fa7f0";
        return "#cccccc";
    }

    // --- rendering -----------------------------------------------------------

    // The scoreboard panel, captured while it is still inside this layout's
    // tree. $("#id") resolves against the layout's panel context, so once the
    // panel is hoisted to the HUD root, $() stops finding anything in it -
    // rev 2 died exactly that way, with every post-hoist lookup failing
    // silently and the status label frozen at its XML default. All lookups
    // therefore go through the captured panel object (NOT Deadworks.Text/Show,
    // which use $() and would hit the same bug), and it stays valid no matter
    // where the panel is parented.
    var scoreboard = null;

    function find(id) {
        if (scoreboard) {
            if (id === "DwScoreboard") return scoreboard;
            if (scoreboard.FindChildTraverse) {
                var p = scoreboard.FindChildTraverse(id);
                if (p) return p;
            }
        }
        return typeof $ !== "undefined" ? $("#" + id) : null;
    }

    function setText(id, text) {
        var label = find(id);
        if (label) label.text = text;
    }

    function setStrip(colour) {
        var strip = find("dw_status_strip");
        if (strip) strip.style.backgroundColor = colour;
    }

    function render(rows, sortColumn) {
        for (var i = 0; i < MAX_ROWS; ++i) {
            var rowPanel = find("dw_row" + i);
            var has = i < rows.length;
            if (rowPanel) rowPanel.visible = has;
            if (!has) continue;

            var row = rows[i];
            var name = find("dw_r" + i + "_name");
            if (name) {
                name.text = String(row.name || "");
                name.style.color = teamColour(row.team);
            }
            setText("dw_r" + i + "_k", row.k || "0");
            setText("dw_r" + i + "_d", row.d || "0");
            setText("dw_r" + i + "_a", row.a || "0");
            setText("dw_r" + i + "_nw", formatNetWorth(row.nw));
            setText("dw_r" + i + "_lvl", row.lvl || "0");
        }

        for (var c = 0; c < COLUMNS.length; ++c) {
            var header = find("dw_sort_" + COLUMNS[c]);
            if (header) header.style.color =
                COLUMNS[c] === sortColumn ? "#00ffff" : "#888888";
        }
    }

    var updates = 0;

    function onUpdate(rows, meta) {
        render(rows.slice(0, MAX_ROWS), meta.sort || "kills");
        ++updates;
        setText("dw_status", "live · " + (meta.reason || "?") + " · #" + updates);
        setStrip("#00ffff");
    }

    function onPong(data) {
        setText("dw_pong", "server clock " + (data.time || "?") + "s");
    }

    // --- actions (wired to onactivate in the layout) -------------------------

    function sort(column) {
        if (typeof Deadworks === "undefined") return;
        Deadworks.SendToServer("scoreboard.sort", { column: column });
    }

    function ping() {
        if (typeof Deadworks === "undefined") return;
        setText("dw_pong", "ping sent...");
        Deadworks.SendToServer("scoreboard.ping", {});
    }

    // --- overlay hoist -------------------------------------------------------

    // Moves #DwScoreboard from inside the (transformed, clipping) health bars
    // container to the HUD root. A previous hoist's host is deleted first, so
    // a layout reload of hud_health never stacks a second overlay - the old
    // panel survives the reload precisely because it no longer lives there.
    function hoist() {
        var panel = scoreboard;
        if (!panel || !panel.SetParent || typeof $.CreatePanel !== "function") {
            $.Msg("[dwscore4] hoist unavailable, staying in place\n");
            return;
        }
        var root = panel;
        while (root.GetParent && root.GetParent()) root = root.GetParent();
        try {
            var old = root.FindChildTraverse && root.FindChildTraverse("DwOverlayHost");
            if (old) old.DeleteAsync(0);
            var host = $.CreatePanel("Panel", root, "DwOverlayHost");
            host.hittest = false;
            host.style.width = "100%";
            host.style.height = "100%";
            panel.SetParent(host);
            // Proves the find() path survived the move; "LOST" here means the
            // rev-2 bug is back in some new form.
            $.Msg("[dwscore4] hoisted overlay to HUD root; post-hoist lookup: " +
                  (find("dw_status") ? "ok" : "LOST") + "\n");
        } catch (err) {
            $.Msg("[dwscore4] hoist failed: " + err + "\n");
        }
    }

    // --- wiring --------------------------------------------------------------

    if (typeof $ !== "undefined") {
        scoreboard = $("#DwScoreboard");
        if (typeof Deadworks === "undefined") {
            // The helper include is missing or failed to compile; say so on
            // the panel instead of dying silently.
            $.Msg("[dwscore4] FAIL - Deadworks helper did not load\n");
            setText("dw_status", "helper missing");
            setStrip("#ff0000");
        } else {
            $.Msg("[dwscore4] loaded, announcing\n");
            setText("dw_status", "waiting for server");
            setStrip("#ff8800");
            hoist();

            Deadworks.SubscribeTable("scoreboard.update", onUpdate);
            Deadworks.Subscribe("scoreboard.pong", onPong);

            // Announces the panel; the server's UiApp marks this player live
            // and answers with the first snapshot (StateRequested), which
            // proves the full round trip without a mouse cursor. Runs on
            // every load and reload.
            Deadworks.Ready(APP_ID);
        }
    }

    return {
        formatNetWorth: formatNetWorth,
        teamColour: teamColour,
        onUpdate: onUpdate,
        onPong: onPong,
        sort: sort,
        ping: ping,
    };
})();

// Lets the pure half run under `bun test`; Panorama has no module system and
// simply skips this.
if (typeof module !== "undefined" && module.exports) module.exports = DwScore;
