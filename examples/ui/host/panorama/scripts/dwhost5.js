// Deadworks server-built UI host runtime.
//
// The panel half of UiApp.SetTree / UpdateNode / ServeTreeFile: receives a
// tree described by the server as rows-as-nodes over the data channel
// (dw.tree.set, chunked; dw.tree.update for in-place changes) and builds it
// with $.CreatePanel. Compiled ONCE into the dwhost bundle; after that, all
// UI comes from the server - no CSDK, no compiling, live edits.
//
// Included by hud_health.xml after the Deadworks helper (dwcore5.js). At load
// it hoists #DwHost into the HUD's own layer (HudCore - above the regular
// HUD, below the escape/settings menu and other game overlays) and grows it
// to full screen; the server's root node positions itself with its own
// align/margin styles.
//
// The filename carries a revision number because the engine never invalidates
// a compiled script: bump the name (dwhost5.js, ...) and the layout's include
// on every script change. v5 adds the dw:// image-source rewrite for
// server-pushed image packs (UiApp.ServeImages, client module v27+).

var DwHost = (function () {
    "use strict";

    // The id servers create their UiApp with; fixed in v1 (one tree host per
    // server).
    var APP_ID = "dwhost";

    // --- pure functions, covered by bun test ---------------------------------

    // "flow-children: down; background-color: #fff" ->
    // [["flowChildren", "down"], ["backgroundColor", "#fff"]]
    // Panorama's JS style object exposes camelCase properties, so the inline
    // string is applied one assignment at a time.
    function parseStyle(text) {
        var out = [];
        if (!text) return out;
        var parts = String(text).split(";");
        for (var i = 0; i < parts.length; ++i) {
            var colon = parts[i].indexOf(":");
            if (colon <= 0) continue;
            var prop = parts[i].slice(0, colon).replace(/^\s+|\s+$/g, "");
            var value = parts[i].slice(colon + 1).replace(/^\s+|\s+$/g, "");
            if (!prop || !value) continue;
            prop = prop.toLowerCase().replace(/-([a-z])/g, function (m, c) {
                return c.toUpperCase();
            });
            out.push([prop, value]);
        }
        return out;
    }

    // "dw://name.png" -> "s2r://panorama/images/dw/name.vtex" - the shorthand
    // for server-pushed images (UiApp.ServeImages). The pack mounts every PNG
    // as a compiled texture under panorama/images/dw/, so the rewrite is pure
    // string work; anything that is not dw:// passes through untouched.
    function rewriteSrc(src) {
        var text = String(src || "");
        if (text.indexOf("dw://") !== 0) return text;
        var stem = text.slice(5);
        if (stem.slice(-4).toLowerCase() === ".png") stem = stem.slice(0, -4);
        return "s2r://panorama/images/dw/" + stem + ".vtex";
    }

    // The style assignments to run on mouse-out: for every property the
    // hover style touched, the base style's value. A hovered property with
    // no base value cannot be restored (there is no computed-style read
    // here), so authors give the base style a value for everything the
    // hover style changes.
    function hoverRevert(baseText, hoverText) {
        var base = parseStyle(baseText);
        var hover = parseStyle(hoverText);
        var out = [];
        for (var i = 0; i < hover.length; ++i) {
            for (var j = 0; j < base.length; ++j) {
                if (base[j][0] === hover[i][0]) {
                    out.push(base[j]);
                    break;
                }
            }
        }
        return out;
    }

    // Buffers dw.tree.set chunks until a revision is complete, then returns
    // the concatenated node rows (global document order - the codec's parent
    // indices depend on it). A different rev replaces the buffer, so a
    // half-received tree is discarded when a newer push starts.
    function makeAssembler() {
        var state = null;
        return {
            push: function (rows, meta) {
                var rev = String(meta.rev || "");
                var of = parseInt(meta.of, 10);
                var index = parseInt(meta.chunk, 10);
                if (!isFinite(of) || of <= 0 || !isFinite(index) || index < 0 || index >= of)
                    return null;

                if (!state || state.rev !== rev || state.of !== of)
                    state = { rev: rev, of: of, chunks: [], got: 0 };

                if (!state.chunks[index]) {
                    state.chunks[index] = rows;
                    state.got++;
                }
                if (state.got < state.of) return null;

                var nodes = [];
                for (var i = 0; i < state.of; ++i)
                    nodes = nodes.concat(state.chunks[i]);
                state = null;
                return nodes;
            },
        };
    }

    // --- rendering -----------------------------------------------------------

    // Captured at load; after the hoist, $("#id") stops resolving into the
    // host (the rev-2 scoreboard bug), so every lookup goes through this.
    var host = null;

    function find(id) {
        if (host) {
            if (id === "DwHost") return host;
            if (host.FindChildTraverse) {
                var p = host.FindChildTraverse(id);
                if (p) return p;
            }
        }
        return typeof $ !== "undefined" ? $("#" + id) : null;
    }

    function setStatus(text, colour) {
        var label = find("dw_status");
        if (label) label.text = text;
        var strip = find("dw_status_strip");
        if (strip) strip.style.backgroundColor = colour;
    }

    function applyProps(panel, props) {
        for (var i = 0; i < props.length; ++i) {
            try {
                panel.style[props[i][0]] = props[i][1];
            } catch (err) {
                $.Msg("[dwhost] style '" + props[i][0] + "' rejected: " + err + "\n");
            }
        }
    }

    function applyStyle(panel, styleText) {
        applyProps(panel, parseStyle(styleText));
    }

    var idMap = {};
    var treeNodes = 0;

    // Reads every TextEntry with an id under `scope` into `payload` -
    // <button submit="form1"> form collection. Inputs named "id" would
    // collide with the click's own id field and are skipped; each value is
    // capped so the whole action stays inside the ~380-byte inbound limit.
    function collectInputs(scope, payload) {
        var kids = scope.Children ? scope.Children() : [];
        for (var i = 0; i < kids.length; ++i) {
            var kid = kids[i];
            try {
                if (kid.paneltype === "TextEntry" && kid.id && kid.id !== "id")
                    payload[kid.id] = String(kid.text || "").slice(0, 120);
            } catch (err) { /* a panel without text access */ }
            collectInputs(kid, payload);
        }
    }

    function wireClick(panel, row, nodeId) {
        // Guarded so a dead API leaves an inert button and a log line,
        // never a broken tree.
        if (typeof panel.SetPanelEvent !== "function") {
            $.Msg("[dwhost] SetPanelEvent unavailable - button '" + nodeId + "' is inert\n");
            return;
        }
        try {
            panel.SetPanelEvent("onactivate", function () {
                var payload = { id: nodeId };
                if (row.sub) {
                    var scope = find(row.sub);
                    if (scope) collectInputs(scope, payload);
                    else $.Msg("[dwhost] submit scope '" + row.sub + "' not found\n");
                }
                Deadworks.SendToServer(row.act, payload);
            });
        } catch (err) {
            $.Msg("[dwhost] wiring button '" + nodeId + "' failed: " + err + "\n");
        }
    }

    // Hover: a style swap (hsty, reverted per-property on mouse-out) and/or
    // server events (hov / hout), on the engine's own mouse events - never
    // polled. Panels only get mouse events while the cursor is free and the
    // panel is hittestable, so hover nodes are made hittestable here.
    function wireHover(panel, row, nodeId) {
        if (typeof panel.SetPanelEvent !== "function") {
            $.Msg("[dwhost] SetPanelEvent unavailable - hover on '" + nodeId + "' is inert\n");
            return;
        }
        var hoverProps = row.hsty ? parseStyle(row.hsty) : [];
        var revertProps = row.hsty ? hoverRevert(row.sty || "", row.hsty) : [];
        try {
            panel.hittest = true;
            panel.SetPanelEvent("onmouseover", function () {
                if (hoverProps.length) applyProps(panel, hoverProps);
                if (row.hov) Deadworks.SendToServer(row.hov, { id: nodeId });
            });
            panel.SetPanelEvent("onmouseout", function () {
                if (revertProps.length) applyProps(panel, revertProps);
                if (row.hout) Deadworks.SendToServer(row.hout, { id: nodeId });
            });
        } catch (err) {
            $.Msg("[dwhost] wiring hover on '" + nodeId + "' failed: " + err + "\n");
        }
    }

    function buildTree(rows) {
        var container = find("DwHostRoot");
        if (!container || typeof $.CreatePanel !== "function") {
            setStatus("cannot build (no CreatePanel)", "#ff0000");
            return;
        }

        // Full rebuild: drop the previous tree's panels, keep the container.
        try {
            if (container.RemoveAndDeleteChildren) container.RemoveAndDeleteChildren();
        } catch (err) {
            $.Msg("[dwhost] clearing old tree failed: " + err + "\n");
        }
        idMap = {};

        var panels = [];
        var built = 0;
        for (var i = 0; i < rows.length; ++i) {
            var row = rows[i];
            if (!row.t) { panels.push(null); continue; }

            var parentIndex = parseInt(row.p, 10);
            var parent = (isFinite(parentIndex) && parentIndex >= 0 && panels[parentIndex])
                ? panels[parentIndex]
                : container;
            var panelId = row.id || ("dw_n" + i);

            // Any Panorama panel type the client can create - Panel, Label,
            // Button, Image, TextEntry, ToggleButton, ... A type the client
            // doesn't know just fails here and is skipped, never fatal.
            var panel;
            try {
                panel = $.CreatePanel(row.t, parent, panelId);
            } catch (err) {
                $.Msg("[dwhost] CreatePanel(" + row.t + ", " + panelId + ") failed: " + err + "\n");
                panels.push(null);
                continue;
            }
            panels.push(panel);
            built++;
            if (row.id) idMap[row.id] = panel;

            if (row.t === "Button") {
                try { panel.hittest = true; } catch (e1) {}
                try { panel.hittestchildren = true; } catch (e2) {}
                if (row.txt) {
                    var label = $.CreatePanel("Label", panel, panelId + "_label");
                    label.text = String(row.txt);
                }
                if (row.act) wireClick(panel, row, row.id || panelId);
            } else if (typeof row.txt !== "undefined") {
                // Label, TextEntry, and any other text-bearing widget.
                try { panel.text = String(row.txt); } catch (eTxt) {}
            }

            if (row.t === "Image" && row.src) {
                try {
                    var imgSrc = rewriteSrc(row.src);
                    if (panel.SetImage) panel.SetImage(imgSrc);
                    else panel.src = imgSrc;
                } catch (err) {
                    $.Msg("[dwhost] image src failed: " + err + "\n");
                }
            }

            if (row.sty) applyStyle(panel, row.sty);
            if (row.hsty || row.hov || row.hout) wireHover(panel, row, row.id || panelId);
            if (row.vis === "0") panel.visible = false;
        }

        treeNodes = built;
        setStatus("tree · " + built + " node(s)", "#00ffff");
    }

    // For button nodes the visible text lives in the generated child label.
    function textTarget(panel, id) {
        var label = panel.FindChildTraverse ? panel.FindChildTraverse(id + "_label") : null;
        return label || panel;
    }

    function applyUpdates(rows) {
        for (var i = 0; i < rows.length; ++i) {
            var row = rows[i];
            var panel = row.id ? idMap[row.id] : null;
            if (!panel) {
                $.Msg("[dwhost] update for unknown node id '" + (row.id || "") + "'\n");
                continue;
            }
            if (typeof row.txt !== "undefined") {
                try { textTarget(panel, row.id).text = String(row.txt); } catch (e1) {}
            }
            if (row.sty) applyStyle(panel, row.sty);
            if (typeof row.vis !== "undefined") panel.visible = row.vis !== "0";
        }
    }

    // --- overlay hoist (the scoreboard-proven pattern) ------------------------

    // Where the overlay belongs. In the stock hud.xml, Panel.HudCore holds
    // the whole in-match HUD, and everything the game stacks over it - the
    // escape/settings menu, popups, tooltips, toasts - comes as LATER
    // SIBLINGS of HudCore. Landing inside HudCore therefore draws above the
    // regular HUD but below all of those. When the class is not in the
    // chain (a game update renamed it), fall back to the window root - the
    // old always-on-top behaviour, never a missing overlay.
    function pickHoistTarget(chain) {
        for (var i = 0; i < chain.length; ++i) {
            var p = chain[i];
            try {
                if (p && p.BHasClass && p.BHasClass("HudCore")) return p;
            } catch (err) { /* a panel without class queries */ }
        }
        return chain.length ? chain[chain.length - 1] : null;
    }

    // Moves #DwHost from inside the (transformed, clipping) health cluster
    // into the HUD layer and grows it to full screen. A previous hoist's
    // overlay is deleted first so a hud_health reload never stacks a second
    // one.
    function hoist() {
        var panel = host;
        if (!panel || !panel.SetParent || typeof $.CreatePanel !== "function") {
            $.Msg("[dwhost] hoist unavailable, staying in place\n");
            return;
        }
        var chain = [];
        var up = panel.GetParent && panel.GetParent();
        while (up) {
            chain.push(up);
            up = up.GetParent && up.GetParent();
        }
        var top = chain.length ? chain[chain.length - 1] : null;
        var target = pickHoistTarget(chain);
        if (!target) {
            $.Msg("[dwhost] hoist found no ancestors, staying in place\n");
            return;
        }
        try {
            // Search from the very top: an overlay from a previous load may
            // sit at the window root (old builds) or in HudCore.
            var old = top && top.FindChildTraverse && top.FindChildTraverse("DwHostOverlay");
            if (old) old.DeleteAsync(0);
            var overlay = $.CreatePanel("Panel", target, "DwHostOverlay");
            overlay.hittest = false;
            try { overlay.hittestchildren = true; } catch (e) {}
            overlay.style.width = "100%";
            overlay.style.height = "100%";
            panel.SetParent(overlay);
            // Safe to fill the screen now that we are out of the cluster.
            panel.style.width = "100%";
            panel.style.height = "100%";
            $.Msg("[dwhost] hoisted into " +
                  (target === top ? "window root (HudCore not found - will draw over menus)" : "HudCore") +
                  "; post-hoist lookup: " + (find("dw_status") ? "ok" : "LOST") + "\n");
        } catch (err) {
            $.Msg("[dwhost] hoist failed: " + err + "\n");
        }
    }

    // --- wiring --------------------------------------------------------------

    if (typeof $ !== "undefined") {
        host = $("#DwHost");
        if (typeof Deadworks === "undefined") {
            $.Msg("[dwhost] FAIL - Deadworks helper did not load\n");
            setStatus("helper missing", "#ff0000");
        } else {
            $.Msg("[dwhost] loaded, announcing\n");
            setStatus("waiting for tree", "#ff8800");
            hoist();

            var assembler = makeAssembler();
            Deadworks.SubscribeTable("dw.tree.set", function (rows, meta) {
                var nodes = assembler.push(rows, meta);
                if (nodes) buildTree(nodes);
            });
            Deadworks.SubscribeTable("dw.tree.update", function (rows) {
                applyUpdates(rows);
            });

            // Announces the panel; the server answers with the current tree
            // (UiApp.StateRequested -> SetTree). Runs on load and reload.
            Deadworks.Ready(APP_ID);
        }
    }

    return {
        parseStyle: parseStyle,
        makeAssembler: makeAssembler,
        buildTree: buildTree,
        applyUpdates: applyUpdates,
        pickHoistTarget: pickHoistTarget,
        hoverRevert: hoverRevert,
        rewriteSrc: rewriteSrc,
    };
})();

// Lets the pure half run under `bun test`; Panorama has no module system and
// simply skips this.
if (typeof module !== "undefined" && module.exports) module.exports = DwHost;
