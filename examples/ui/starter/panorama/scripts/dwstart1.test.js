// Tests for the pure half of the starter panel script.
//
// Run: bun test   (from examples/ui)

const { test, expect } = require("bun:test");
const { helloText } = require("./dwstart1.js");

test("formats a hello payload for display", () => {
    expect(helloText({ message: "welcome", time: "613" })).toEqual({
        message: "welcome",
        clock: "server clock 613s",
    });
});

test("degrades gracefully on missing fields", () => {
    expect(helloText({})).toEqual({ message: "", clock: "server clock ?s" });
    expect(helloText(null)).toEqual({ message: "", clock: "server clock ?s" });
});
