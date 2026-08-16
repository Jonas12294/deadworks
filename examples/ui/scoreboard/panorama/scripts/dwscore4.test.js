// Tests for the pure half of the scoreboard panel script.
//
// Run: bun test   (from examples/ui)
//
// Row decoding itself lives in the Deadworks helper now
// (Deadworks.SubscribeTable / tableFromData, tested in deadworks.test.js);
// what remains here is the scoreboard's own display logic.

const { test, expect } = require("bun:test");
const { formatNetWorth, teamColour } = require("./dwscore4.js");
const { parsePayload, tableFromData } = require("../../../deadworks.js");

test("survives the full wire format from the server", () => {
    // Byte-identical to what UiPayload/UiTable produce for a scoreboard emit.
    const text =
        "v=1\nevent=scoreboard.update\nd.n=1\n" +
        "d.r0.name=alice\nd.r0.team=2\nd.r0.k=3\nd.r0.d=1\nd.r0.a=2\n" +
        "d.r0.nw=4200\nd.r0.lvl=9\n" +
        "d.reason=timer\nd.sort=kills\n";

    const payload = parsePayload(text);
    expect(payload.event).toBe("scoreboard.update");

    const { rows, meta } = tableFromData(payload.data);
    expect(rows).toEqual([
        { name: "alice", team: "2", k: "3", d: "1", a: "2", nw: "4200", lvl: "9" },
    ]);
    expect(meta).toEqual({ reason: "timer", sort: "kills" });
});

test("formats net worth compactly", () => {
    expect(formatNetWorth("950")).toBe("950");
    expect(formatNetWorth("1000")).toBe("1k");
    expect(formatNetWorth("12345")).toBe("12.3k");
});

test("treats malformed net worth as zero", () => {
    expect(formatNetWorth(undefined)).toBe("0");
    expect(formatNetWorth("garbage")).toBe("0");
    expect(formatNetWorth("-5")).toBe("0");
});

test("colours the two teams and falls back to neutral", () => {
    expect(teamColour("2")).toBe("#f0b232");
    expect(teamColour("3")).toBe("#3fa7f0");
    expect(teamColour("0")).toBe("#cccccc");
    expect(teamColour(undefined)).toBe("#cccccc");
});
