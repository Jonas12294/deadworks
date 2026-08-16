// Probe v4: render the compiler's texture (A, known good) beside our
// C#-format generated texture (B). B rendering proves the encoder.

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

    var candidates = [
        ["a", "s2r://panorama/images/dwtest/probe.vtex"],
        ["b", "s2r://panorama/images/dwtest/gen.vtex"],
    ];

    for (var i = 0; i < candidates.length; ++i) {
        var key = candidates[i][0];
        var path = candidates[i][1];
        var verdict;
        try {
            verdict = typeof $.BImageFileExists === "function"
                ? ($.BImageFileExists(path) ? "exists TRUE" : "exists false")
                : "(no api)";
        } catch (err) {
            verdict = "threw: " + err;
        }
        var label = byId("verdict_" + key);
        if (label) label.text = key.toUpperCase() + ": " + verdict;
        if ($.Msg) $.Msg("[imgprobe] " + key + " " + path + " -> " + verdict + "\n");

        var img = byId("img_" + key);
        if (img) {
            try {
                if (img.SetImage) img.SetImage(path);
                else img.src = path;
            } catch (err2) {
                if ($.Msg) $.Msg("[imgprobe] SetImage " + key + " threw: " + err2 + "\n");
            }
        }
    }
})();
