// The "hello" UI's script. Small on purpose - shows that each UI folder carries
// its own JS. Confirms a server round trip (test.pong) into the status line.

(function () {
    "use strict";

    var ROOT = (function () {
        var p = $.GetContextPanel ? $.GetContextPanel() : null;
        while (p && p.GetParent && p.GetParent()) p = p.GetParent();
        return p;
    })();
    function byId(id) {
        return ROOT && ROOT.FindChildTraverse ? ROOT.FindChildTraverse(id) : null;
    }

    if (typeof Deadworks !== "undefined" && Deadworks.Subscribe) {
        // Start clean - drop the previous UI's subscriptions.
        if (Deadworks.Reset) Deadworks.Reset();
        Deadworks.Subscribe("test.pong", function (d) {
            var s = byId("status");
            if (s) s.text = "server says pong · clock " + (d.time || "?") + "s";
        });
        // Prove the panel is talking to the server the moment it loads.
        Deadworks.SendToServer("test.ping", {});
    }

    if ($.Msg) $.Msg("[hello ui.js] loaded\n");
})();
