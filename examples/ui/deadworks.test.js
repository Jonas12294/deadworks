// Tests for the pure half of deadworks.js - the wire format shared with the
// server's UiPayload (managed/DeadworksManaged.Api/UI/UiPayload.cs).
//
// Run: bun test   (from examples/ui)
//
// Only the pure functions are covered here. The $.RegisterForUnhandledEvent
// wiring needs Panorama's V8 and is verified in game.

const { test, expect } = require("bun:test");
const {
    parsePayload,
    decodeBase64,
    Subscribe,
    SubscribeTable,
    tableFromData,
    handleRaw,
    encodePayload,
    FreeCursor,
    MousePosition,
    OnHover,
} = require("./deadworks.js");

test("MousePosition converts window pixels into panel units via the UI scale", () => {
    // $.MousePosition() returns raw window pixels (verified in panorama.dll:
    // the binding fills two window ints, no scaling). Panel layout units
    // differ by actualuiscale, so the helper divides it out.
    globalThis.$ = { MousePosition: () => ({ x: 300, y: 150 }) };
    try {
        const panel = { actualuiscale_x: 1.5, actualuiscale_y: 1.5 };
        expect(MousePosition(panel)).toEqual({ x: 200, y: 100, windowX: 300, windowY: 150 });
    } finally {
        delete globalThis.$;
    }
});

test("MousePosition falls back to the context panel's scale, then to 1", () => {
    globalThis.$ = {
        MousePosition: () => ({ x: 100, y: 50 }),
        GetContextPanel: () => ({ actualuiscale_x: 2, actualuiscale_y: 2 }),
    };
    try {
        expect(MousePosition()).toEqual({ x: 50, y: 25, windowX: 100, windowY: 50 });

        globalThis.$ = { MousePosition: () => ({ x: 100, y: 50 }) };
        expect(MousePosition()).toEqual({ x: 100, y: 50, windowX: 100, windowY: 50 });
    } finally {
        delete globalThis.$;
    }
});

test("MousePosition is null outside a panel context", () => {
    expect(MousePosition()).toBeNull();
});

test("OnHover registers engine hover events instead of polling", () => {
    // onmouseover/onmouseout are real panel event types (listed in
    // panorama.dll's event-name resolver), so hover never needs a per-frame
    // coordinate loop.
    const events = [];
    const panel = { SetPanelEvent: (name, fn) => events.push([name, fn]) };
    const over = () => {};
    const out = () => {};

    expect(OnHover(panel, over, out)).toBe(true);
    expect(events).toEqual([["onmouseover", over], ["onmouseout", out]]);

    expect(OnHover(null, over, out)).toBe(false);
    expect(OnHover({}, over, out)).toBe(false);
});

test("FreeCursor dispatches the stock hud_free_cursor toggle", () => {
    const calls = [];
    globalThis.$ = { DispatchEvent: (...args) => calls.push(args) };
    try {
        FreeCursor(true);
        FreeCursor(false);
        expect(calls).toEqual([
            ["CitadelConCommand", "hud_free_cursor 1"],
            ["CitadelConCommand", "hud_free_cursor 0"],
        ]);
    } finally {
        delete globalThis.$;
    }
});

// Decoded with Buffer rather than the module's own decoder, so the encode path
// is checked against an independent implementation.
const unb64 = (s) => Buffer.from(s, "base64").toString("utf8");

// Encoded with Buffer rather than the module's own encoder, so the decode path
// is checked against an independent implementation.
const b64 = (s) => Buffer.from(s, "utf8").toString("base64");

test("parses an emit payload into its event name and data", () => {
    const text = "v=1\nevent=scoreboard.update\nd.reason=death\nd.kills=7\n";

    expect(parsePayload(text)).toEqual({
        event: "scoreboard.update",
        data: { reason: "death", kills: "7" },
    });
});

test("rejects a payload whose version it does not recognise", () => {
    expect(parsePayload("v=2\nevent=scoreboard.update\n")).toBeNull();
});

test("rejects a payload with no version line", () => {
    expect(parsePayload("event=scoreboard.update\n")).toBeNull();
});

test("keeps '=' characters that appear inside a value", () => {
    const text = "v=1\nevent=e\nd.token=a=b=c\n";

    expect(parsePayload(text).data.token).toBe("a=b=c");
});

// --- base64 -----------------------------------------------------------------
// The module base64-encodes the payload before handing it to Panorama, so it
// survives the event-string parser. Panorama's V8 has no atob().

test("decodes a base64 payload back to the text the server sent", () => {
    const b64 = "dj0xCmV2ZW50PXNjb3JlYm9hcmQudXBkYXRlCmQucmVhc29uPWRlYXRoCg==";

    expect(decodeBase64(b64)).toBe("v=1\nevent=scoreboard.update\nd.reason=death\n");
});

test("decodes multi-byte utf-8 correctly", () => {
    expect(decodeBase64("Wm/DqyDinJM=")).toBe("Zoë ✓");
});

test("decodes input padded with a single '='", () => {
    expect(decodeBase64("YWI=")).toBe("ab");
});

test("returns null for input that is not base64", () => {
    expect(decodeBase64("not!base64")).toBeNull();
});

// --- routing ----------------------------------------------------------------
// Every emit rides one shared Panorama event; the payload's own event name is
// what picks the callback. Each test uses a distinct name because subscriptions
// live for the lifetime of the panel.

test("delivers the data to a callback subscribed to that event", () => {
    let got = null;
    Subscribe("t.deliver", (data) => { got = data; });

    handleRaw(b64("v=1\nevent=t.deliver\nd.score=12\n"));

    expect(got).toEqual({ score: "12" });
});

test("does not deliver to a callback subscribed to a different event", () => {
    let called = false;
    Subscribe("t.other", () => { called = true; });

    handleRaw(b64("v=1\nevent=t.notother\nd.x=1\n"));

    expect(called).toBe(false);
});

test("delivers to every callback subscribed to the same event", () => {
    const seen = [];
    Subscribe("t.multi", () => seen.push("first"));
    Subscribe("t.multi", () => seen.push("second"));

    handleRaw(b64("v=1\nevent=t.multi\n"));

    expect(seen).toEqual(["first", "second"]);
});

test("a callback that throws does not stop the ones after it", () => {
    let reached = false;
    Subscribe("t.throws", () => { throw new Error("boom"); });
    Subscribe("t.throws", () => { reached = true; });

    handleRaw(b64("v=1\nevent=t.throws\n"));

    expect(reached).toBe(true);
});

test("ignores a dispatch whose argument is not base64", () => {
    let called = false;
    Subscribe("t.garbage", () => { called = true; });

    expect(() => handleRaw("not!base64")).not.toThrow();
    expect(called).toBe(false);
});

test("finds the payload when the engine passes the panel first", () => {
    // Unhandled-event handlers receive the target panel ahead of the event's own
    // arguments, so the payload is not always the first parameter.
    let got = null;
    Subscribe("t.panelfirst", (data) => { got = data; });

    const fakePanel = { id: "some_panel" };
    handleRaw(fakePanel, b64("v=1\nevent=t.panelfirst\nd.score=9\n"));

    expect(got).toEqual({ score: "9" });
});

test("ignores a payload with an unrecognised version", () => {
    let called = false;
    Subscribe("t.version", () => { called = true; });

    handleRaw(b64("v=99\nevent=t.version\n"));

    expect(called).toBe(false);
});

// --- encoding (the panel -> server direction) --------------------------------

test("encodes an action payload in the format the server parses", () => {
    const encoded = encodePayload("scoreboard.sort", { column: "kills" });

    expect(unb64(encoded)).toBe("v=1\nevent=scoreboard.sort\nd.column=kills\n");
});

test("encodes multi-byte utf-8 values", () => {
    const encoded = encodePayload("e", { name: "Zoë ✓" });

    expect(unb64(encoded)).toBe("v=1\nevent=e\nd.name=Zoë ✓\n");
});

test("strips newlines from a value so it cannot forge extra payload fields", () => {
    const encoded = encodePayload("e", { note: "hi\nd.injected=1" });

    expect(unb64(encoded)).toBe("v=1\nevent=e\nd.note=hid.injected=1\n");
});

test("round-trips through its own decoder", () => {
    const encoded = encodePayload("t.roundtrip", { a: "1", b: "two" });

    expect(parsePayload(decodeBase64(encoded))).toEqual({
        event: "t.roundtrip",
        data: { a: "1", b: "two" },
    });
});

// --- tables ------------------------------------------------------------------
// The flat n= / r<i>.<field>= encoding is written by the server's UiTable
// (managed/DeadworksManaged.Api/UI/UiTable.cs); these fixtures match its
// ToData() output byte for byte.

test("rebuilds rows and meta from a UiTable payload", () => {
    const data = {
        n: "2",
        "r0.name": "Alice", "r0.k": "5",
        "r1.name": "Bob", "r1.k": "2",
        sort: "kills",
    };

    expect(tableFromData(data)).toEqual({
        rows: [
            { name: "Alice", k: "5" },
            { name: "Bob", k: "2" },
        ],
        meta: { sort: "kills" },
    });
});

test("drops row keys at or past the declared count", () => {
    const data = { n: "1", "r0.name": "Alice", "r5.name": "smuggled" };

    expect(tableFromData(data).rows).toEqual([{ name: "Alice" }]);
});

test("skips declared rows that have no fields", () => {
    const data = { n: "3", "r0.name": "Alice", "r2.name": "Cara" };

    expect(tableFromData(data).rows).toEqual([{ name: "Alice" }, { name: "Cara" }]);
});

test("an empty table is empty rows and empty meta", () => {
    expect(tableFromData({ n: "0" })).toEqual({ rows: [], meta: {} });
    expect(tableFromData(null)).toEqual({ rows: [], meta: {} });
    expect(tableFromData({ n: "garbage" })).toEqual({ rows: [], meta: {} });
});

test("keeps dots inside row field names", () => {
    // UiTable allows '.' in a field key; only the first r<i>. prefix is structural.
    const data = { n: "1", "r0.stat.k": "5" };

    expect(tableFromData(data).rows).toEqual([{ "stat.k": "5" }]);
});

test("SubscribeTable delivers decoded rows and meta", () => {
    let rows = null, meta = null;
    SubscribeTable("t.table", (r, m) => { rows = r; meta = m; });

    handleRaw(b64("v=1\nevent=t.table\nd.n=1\nd.r0.name=Alice\nd.sort=kills\n"));

    expect(rows).toEqual([{ name: "Alice" }]);
    expect(meta).toEqual({ sort: "kills" });
});
