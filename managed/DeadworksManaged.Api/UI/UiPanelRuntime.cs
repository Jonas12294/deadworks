namespace DeadworksManaged.Api;

/// <summary>
/// Panel-side helpers the server installs into every panel that announces
/// itself, so hand-written <c>ui.js</c> can create panels without knowing how
/// the host lays itself out.
/// </summary>
/// <remarks>
/// <para>This ships over the script channel rather than in the host bundle
/// because it can then reach players immediately, without republishing content
/// — the same reason server-built UI exists at all. It augments the
/// <c>Deadworks</c> global the bundle already provides and is a no-op wherever
/// that global (or the client module) is absent.</para>
/// <para>It answers the two things that make <c>$.CreatePanel</c> appear to do
/// nothing here: the host <b>re-parents itself</b> into <c>HudCore</c> at load,
/// so a panel created under the window root exists but is never drawn; and
/// every tree push <b>deletes the children</b> of the tree container, so
/// panels made there vanish on the next state push. <c>Deadworks.Container</c>
/// puts yours beside the tree instead of inside it.</para>
/// </remarks>
internal static class UiPanelRuntime
{
	/// <summary>
	/// The script, sent once per panel announcement. Written in the same ES5
	/// style as the bundled helper, guards every engine call, and defines
	/// nothing when the helper is missing.
	/// </summary>
	internal const string Script = """
(function () {
    if (typeof $ === "undefined" || typeof Deadworks === "undefined") return;

    // The host panel this script is running inside. The server-built tree, the
    // status strip and everything else live under it.
    function hostPanel() {
        var panel = $.GetContextPanel ? $.GetContextPanel() : null;
        var walk = panel;
        while (walk) {
            if (walk.id === "DwHost") return walk;
            walk = walk.GetParent ? walk.GetParent() : null;
        }
        return panel;
    }

    // Creates a panel by id, replacing whatever was there under that id. Panel
    // scripts re-run on every state push, so plain creation would stack
    // duplicates; this makes a re-run idempotent.
    Deadworks.Create = function (type, parent, id) {
        if (!parent || typeof $.CreatePanel !== "function") return null;
        if (id && parent.FindChildTraverse) {
            var old = parent.FindChildTraverse(id);
            if (old && old.DeleteAsync) old.DeleteAsync(0);
        }
        try {
            return $.CreatePanel(type, parent, id || "");
        } catch (err) {
            $.Msg("[dw] CreatePanel(" + type + ") failed: " + err + "\n");
            return null;
        }
    };

    // The container the server's own tree is built into. Anything added here
    // is DELETED on the next tree push - use it to read the tree, not to hold
    // your own panels.
    Deadworks.Root = function () {
        var host = hostPanel();
        if (host && host.FindChildTraverse) {
            var root = host.FindChildTraverse("DwHostRoot");
            if (root) return root;
        }
        return host;
    };

    // A container of your own, beside the server's tree rather than inside it,
    // so a tree push does not delete it. Call it again with the same id to
    // start over. Pass false to put it inside the tree container instead.
    Deadworks.Container = function (id, besideTree) {
        var host = hostPanel();
        if (!host) return null;
        var parent = (besideTree === false)
            ? Deadworks.Root()
            : (host.GetParent ? host.GetParent() : host);
        return Deadworks.Create("Panel", parent, id || "DwUserContainer");
    };
})();
""";
}
