// One QA agent's screen: a Playwright page at a fixed size, driven by the members of Claude's computer toolset
// (computer_toolset_20260801). Every action happens on this page only; the page's context refuses other origins.
"use strict";

const NOT_EXECUTED = "Not executed: an earlier computer action in this turn failed.";

// xdotool-style key names (what the model sends) to Playwright key names.
const KEYS = {
  return: "Enter", enter: "Enter", kp_enter: "Enter", tab: "Tab", escape: "Escape", esc: "Escape", backspace: "Backspace",
  delete: "Delete", space: " ", up: "ArrowUp", down: "ArrowDown", left: "ArrowLeft", right: "ArrowRight",
  page_up: "PageUp", page_down: "PageDown", prior: "PageUp", next: "PageDown", home: "Home", end: "End", insert: "Insert",
  ctrl: "Control", control: "Control", alt: "Alt", shift: "Shift", super: "Meta", cmd: "Meta", meta: "Meta",
};
const MODIFIERS = { shift: "Shift", ctrl: "Control", control: "Control", alt: "Alt", super: "Meta", meta: "Meta", cmd: "Meta" };

function keyName(part) {
  const k = part.trim();
  const mapped = KEYS[k.toLowerCase()];
  if (mapped) return mapped;
  if (/^f\d{1,2}$/i.test(k)) return k.toUpperCase();
  return k.length === 1 ? k : k.charAt(0).toUpperCase() + k.slice(1);
}
/** "ctrl+shift+t" → "Control+Shift+t" */
function combo(text) { return String(text).split("+").map(keyName).join("+"); }
function modifiers(text) { return text ? String(text).split("+").map((m) => MODIFIERS[m.trim().toLowerCase()]).filter(Boolean) : []; }

class Screen {
  constructor(page, { width = 1280, height = 800, onShot } = {}) {
    this.page = page;
    this.width = width;
    this.height = height;
    this.cursor = [Math.round(width / 2), Math.round(height / 2)];
    this.onShot = onShot || (() => {});
    this.actions = 0;
  }

  point(coordinate) {
    if (!coordinate) return this.cursor;
    const [x, y] = coordinate.map(Number);
    if (!Number.isFinite(x) || !Number.isFinite(y) || x < 0 || y < 0 || x >= this.width || y >= this.height)
      throw new Error(`coordinate ${JSON.stringify(coordinate)} is outside the ${this.width}x${this.height} screen`);
    this.cursor = [Math.round(x), Math.round(y)];
    return this.cursor;
  }

  async withModifiers(text, fn) {
    const mods = modifiers(text);
    for (const m of mods) await this.page.keyboard.down(m);
    try { return await fn(); } finally { for (const m of mods.reverse()) await this.page.keyboard.up(m); }
  }

  async shot(region) {
    const png = await this.page.screenshot(region
      ? { type: "png", clip: { x: region[0], y: region[1], width: Math.max(1, region[2] - region[0]), height: Math.max(1, region[3] - region[1]) } }
      : { type: "png" });
    this.onShot(png);
    return [{ type: "image", source: { type: "base64", media_type: "image/png", data: png.toString("base64") } }];
  }

  async settle() { await this.page.waitForLoadState("domcontentloaded").catch(() => {}); await this.page.waitForTimeout(250); }

  /** Runs one member; returns tool_result content. Throws on failure (the caller marks it is_error and halts the batch). */
  async run(name, input = {}) {
    this.actions++;
    const p = this.page;
    switch (name) {
      case "screenshot": return this.shot();
      case "zoom": {
        const r = input.region;
        if (!Array.isArray(r) || r.length !== 4) throw new Error("zoom needs region [x0, y0, x1, y1]");
        this.point([r[0], r[1]]); this.point([Math.max(r[0], r[2] - 1), Math.max(r[1], r[3] - 1)]);
        return this.shot(r);
      }
      case "left_click": case "right_click": case "middle_click": case "double_click": case "triple_click": {
        const [x, y] = this.point(input.coordinate);
        const button = name === "right_click" ? "right" : name === "middle_click" ? "middle" : "left";
        const clickCount = name === "double_click" ? 2 : name === "triple_click" ? 3 : 1;
        await this.withModifiers(input.text, () => p.mouse.click(x, y, { button, clickCount }));
        await this.settle();
        return [{ type: "text", text: "OK" }];
      }
      case "left_click_drag": {
        const [sx, sy] = this.point(input.start_coordinate);
        const [ex, ey] = this.point(input.coordinate);
        await this.withModifiers(input.text, async () => {
          await p.mouse.move(sx, sy); await p.mouse.down(); await p.mouse.move(ex, ey, { steps: 12 }); await p.mouse.up();
        });
        await this.settle();
        return [{ type: "text", text: "OK" }];
      }
      case "mouse_move": { const [x, y] = this.point(input.coordinate); await p.mouse.move(x, y); return [{ type: "text", text: "OK" }]; }
      case "left_mouse_down": await p.mouse.down(); return [{ type: "text", text: "OK" }];
      case "left_mouse_up": await p.mouse.up(); await this.settle(); return [{ type: "text", text: "OK" }];
      case "cursor_position": return [{ type: "text", text: `X=${this.cursor[0]}, Y=${this.cursor[1]}` }];
      case "scroll": {
        const [x, y] = this.point(input.coordinate);
        const amount = Math.max(1, Math.min(30, Number(input.scroll_amount) || 1)) * 100;
        const d = { up: [0, -amount], down: [0, amount], left: [-amount, 0], right: [amount, 0] }[input.scroll_direction];
        if (!d) throw new Error(`scroll_direction must be up, down, left or right, not ${input.scroll_direction}`);
        await p.mouse.move(x, y);
        await this.withModifiers(input.text, () => p.mouse.wheel(d[0], d[1]));
        await p.waitForTimeout(250);
        return [{ type: "text", text: "OK" }];
      }
      case "type": await p.keyboard.type(String(input.text ?? ""), { delay: 5 }); return [{ type: "text", text: "OK" }];
      case "key": {
        const repeat = Math.max(1, Math.min(100, Number(input.repeat) || 1));
        for (let i = 0; i < repeat; i++) await p.keyboard.press(combo(input.text));
        await this.settle();
        return [{ type: "text", text: "OK" }];
      }
      case "hold_key": {
        const keys = String(input.text).split("+").map(keyName);
        for (const k of keys) await p.keyboard.down(k);
        await p.waitForTimeout(Math.min(5, Number(input.duration) || 0) * 1000);
        for (const k of keys.reverse()) await p.keyboard.up(k);
        return [{ type: "text", text: "OK" }];
      }
      case "wait": await p.waitForTimeout(Math.min(10, Math.max(0, Number(input.duration) || 1)) * 1000); return [{ type: "text", text: "OK" }];
      default: throw new Error(`${name} is not supported on this screen`);
    }
  }
}

/**
 * Runs a turn's computer calls in order. A failed call is reported with is_error and every later computer call in
 * the same turn gets the halt text, as the toolset's agent loop requires. Every result echoes toolset_name.
 */
async function runComputerCalls(screen, blocks) {
  const results = [];
  let failed = false;
  for (const block of blocks) {
    const result = { type: "tool_result", tool_use_id: block.id, toolset_name: "computer" };
    if (failed) {
      results.push({ ...result, is_error: true, content: NOT_EXECUTED });
      continue;
    }
    try {
      result.content = await screen.run(block.name, block.input || {});
    } catch (err) {
      Object.assign(result, { is_error: true, content: `Error: ${err.message}` });
      failed = true;
    }
    results.push(result);
  }
  return results;
}

module.exports = { Screen, runComputerCalls, combo, NOT_EXECUTED };
