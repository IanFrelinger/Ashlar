// Self-test of the synthetic guard: it must refuse anything but an explicit synthetic: true.
"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const { requireSynthetic, NotSyntheticError } = require("./studio.cjs");

const fake = (body, ok = true) => async () => ({ ok, status: ok ? 200 : 500, json: async () => body });

test("a synthetic studio passes", async () => {
  const meta = await requireSynthetic("http://x", fake({ synthetic: true, build: "b" }));
  assert.equal(meta.build, "b");
});

for (const [name, body, ok] of [["real", { synthetic: false }, true], ["missing flag", {}, true], ["truthy string", { synthetic: "true" }, true], ["error", {}, false]]) {
  test(`a ${name} target is refused`, async () => {
    await assert.rejects(requireSynthetic("http://x", fake(body, ok)), NotSyntheticError);
  });
}

test("an unreachable target is refused", async () => {
  await assert.rejects(requireSynthetic("http://x", async () => { throw new Error("ECONNREFUSED"); }), NotSyntheticError);
});
