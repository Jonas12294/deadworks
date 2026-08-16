// Tests for the pure half of the host runtime - style parsing and chunk
// reassembly. Building panels needs Panorama's $ and is verified in game.
//
// Run: bun test   (from examples/ui)

const { test, expect } = require("bun:test");
const { parseStyle, makeAssembler, pickHoistTarget, hoverRevert, rewriteSrc } = require("./dwhost5.js");

// --- dw:// image sources -------------------------------------------------------

// Server-pushed images (UiApp.ServeImages) mount at panorama/images/dw/ as
// compiled textures; dw://name.png is the author-facing shorthand.

test("rewrites dw:// pngs to the mounted texture path", () => {
    expect(rewriteSrc("dw://icon.png")).toBe("s2r://panorama/images/dw/icon.vtex");
    expect(rewriteSrc("dw://logo")).toBe("s2r://panorama/images/dw/logo.vtex");   // extension optional
});

test("leaves every other source untouched", () => {
    expect(rewriteSrc("s2r://panorama/images/hud/x.vsvg")).toBe("s2r://panorama/images/hud/x.vsvg");
    expect(rewriteSrc("file://{images}/foo.png")).toBe("file://{images}/foo.png");
    expect(rewriteSrc("")).toBe("");
});

// --- hover style revert --------------------------------------------------------

test("mouse-out restores the base value of every hovered property", () => {
    expect(hoverRevert(
        "background-color: #111; color: #fff;",
        "background-color: #333; transform: scaleX(1.05);"
    )).toEqual([
        ["backgroundColor", "#111"],
        // transform has no base value to restore - it stays, which is why
        // the docs say: give the base style a value for every property the
        // hover style touches.
    ]);
});

test("hover revert with no base style restores nothing", () => {
    expect(hoverRevert("", "color: #0ff;")).toEqual([]);
    expect(hoverRevert(undefined, "color: #0ff;")).toEqual([]);
});

// --- hoist target --------------------------------------------------------------

// Stand-ins for the ancestor chain (nearest first, window root last).
const withClass = (name) => ({ BHasClass: (c) => c === name });

test("hoists into the HUD's own layer so game menus stay on top", () => {
    // Stock hud.xml: HudCore holds the in-match HUD; the escape/settings
    // menu, popups, tooltips and toasts are LATER SIBLINGS of it. Landing
    // inside HudCore draws above the HUD but below all of those.
    const hudCore = withClass("HudCore");
    const chain = [withClass("bars_container"), hudCore, withClass("WindowRoot")];

    expect(pickHoistTarget(chain)).toBe(hudCore);
});

test("falls back to the window root when HudCore is not found", () => {
    // A game update renaming the class degrades to the old always-on-top
    // behaviour - never to a missing overlay.
    const root = withClass("WindowRoot");
    expect(pickHoistTarget([withClass("something"), root])).toBe(root);
    expect(pickHoistTarget([])).toBeNull();
});

test("tolerates ancestors without class queries", () => {
    const hudCore = withClass("HudCore");
    expect(pickHoistTarget([{}, null, hudCore])).toBe(hudCore);
});

// --- style strings ------------------------------------------------------------

test("parses an inline style string into camelCase assignments", () => {
    expect(parseStyle("flow-children: down; background-color: #fff;")).toEqual([
        ["flowChildren", "down"],
        ["backgroundColor", "#fff"],
    ]);
});

test("keeps values intact and trims whitespace", () => {
    expect(parseStyle("  border : 2px solid #00ffff66 ")).toEqual([
        ["border", "2px solid #00ffff66"],
    ]);
});

test("skips malformed segments instead of throwing", () => {
    expect(parseStyle("width: 10px; nonsense; : bare; height: 2px")).toEqual([
        ["width", "10px"],
        ["height", "2px"],
    ]);
});

test("empty or missing style yields no assignments", () => {
    expect(parseStyle("")).toEqual([]);
    expect(parseStyle(undefined)).toEqual([]);
});

// --- chunk reassembly ----------------------------------------------------------

const meta = (rev, chunk, of) => ({ rev: String(rev), chunk: String(chunk), of: String(of) });

test("a single-chunk tree completes immediately", () => {
    const a = makeAssembler();
    expect(a.push([{ t: "Panel", p: "-1" }], meta(1, 0, 1))).toEqual([{ t: "Panel", p: "-1" }]);
});

test("chunks arriving out of order still concatenate in chunk order", () => {
    const a = makeAssembler();
    expect(a.push([{ t: "Label" }], meta(1, 1, 2))).toBeNull();
    expect(a.push([{ t: "Panel" }], meta(1, 0, 2))).toEqual([{ t: "Panel" }, { t: "Label" }]);
});

test("a newer revision discards a half-received one", () => {
    const a = makeAssembler();
    expect(a.push([{ t: "Panel" }], meta(1, 0, 2))).toBeNull();   // rev 1 never completes
    expect(a.push([{ t: "Image" }], meta(2, 0, 2))).toBeNull();
    expect(a.push([{ t: "Label" }], meta(2, 1, 2))).toEqual([{ t: "Image" }, { t: "Label" }]);
});

test("a duplicate chunk does not complete a set early", () => {
    const a = makeAssembler();
    expect(a.push([{ t: "Panel" }], meta(1, 0, 2))).toBeNull();
    expect(a.push([{ t: "Panel" }], meta(1, 0, 2))).toBeNull();   // same chunk again
});

test("rejects meta it cannot trust", () => {
    const a = makeAssembler();
    expect(a.push([{ t: "Panel" }], { rev: "1", chunk: "5", of: "2" })).toBeNull(); // out of range
    expect(a.push([{ t: "Panel" }], { rev: "1", chunk: "0", of: "wat" })).toBeNull();
    expect(a.push([{ t: "Panel" }], {})).toBeNull();
});

test("completing a revision resets the assembler for the next one", () => {
    const a = makeAssembler();
    a.push([{ t: "Panel" }], meta(1, 0, 1));
    expect(a.push([{ t: "Label" }], meta(2, 0, 1))).toEqual([{ t: "Label" }]);
});
