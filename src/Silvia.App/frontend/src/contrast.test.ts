import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { test } from "node:test";
import assert from "node:assert/strict";

// The WCAG contrast check: every text pair in both themes must reach AA (4.5:1 for text, 3:1 for
// large text and graphics). It reads the real token sheet, so a changed color cannot slip past it.

type Palette = Record<string, string>;

const css = readFileSync(fileURLToPath(new URL("./tokens.css", import.meta.url)), "utf8");

function block(selector: string): Palette {
  const start = css.indexOf(`${selector} {`);
  assert.notEqual(start, -1, `token block ${selector} is missing`);
  const body = css.slice(start + selector.length + 2, css.indexOf("}", start));
  const palette: Palette = {};
  for (const match of body.matchAll(/--([a-z0-9-]+):\s*(#[0-9a-fA-F]{6})\s*;/g)) {
    palette[match[1]] = match[2];
  }
  return palette;
}

function luminance(hex: string): number {
  const channels = [1, 3, 5].map((i) => {
    const value = parseInt(hex.slice(i, i + 2), 16) / 255;
    return value <= 0.03928 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2];
}

function contrast(a: string, b: string): number {
  const [light, dark] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (light + 0.05) / (dark + 0.05);
}

const light = block(":root");
const dark = block(':root[data-theme="dark"]');

const textPairs: Array<[string, string]> = [
  ["text", "bg"],
  ["text", "surface"],
  ["text", "surface-2"],
  ["text", "code-bg"],
  ["text-muted", "bg"],
  ["text-muted", "surface"],
  ["text-muted", "surface-2"],
  ["text", "sidebar"],
  ["text-muted", "sidebar"],
  ["text", "accent-soft"],
  ["accent", "bg"],
  ["accent", "surface"],
  ["accent", "accent-soft"],
  ["accent-text", "accent"],
  ["accent-text", "accent-hover"],
  ["citation", "citation-bg"],
  ["citation", "surface"],
  ["success", "success-bg"],
  ["success", "surface"],
  ["danger", "bg"],
  ["danger", "danger-bg"],
  ["surface", "danger"],
  ["warn", "warn-bg"],
];

const graphicPairs: Array<[string, string]> = [
  ["focus", "bg"],
  ["focus", "surface"],
  ["focus", "sidebar"],
  ["score", "surface"],
  ["rubric", "sidebar"],
  ["rubric", "surface"],
  ["border-strong", "surface"],
  ["border-strong", "bg"],
];

for (const [name, palette] of [["light", light], ["dark", dark]] as const) {
  test(`${name} text pairs reach WCAG AA (4.5:1)`, () => {
    for (const [fg, bg] of textPairs) {
      const ratio = contrast(palette[fg], palette[bg]);
      assert.ok(ratio >= 4.5, `${name}: --${fg} on --${bg} is ${ratio.toFixed(2)}:1, needs 4.5:1`);
    }
  });

  test(`${name} graphics and focus reach 3:1`, () => {
    for (const [fg, bg] of graphicPairs) {
      const ratio = contrast(palette[fg], palette[bg]);
      assert.ok(ratio >= 3, `${name}: --${fg} on --${bg} is ${ratio.toFixed(2)}:1, needs 3:1`);
    }
  });
}

test("both themes define the same tokens", () => {
  assert.deepEqual(Object.keys(light).sort(), Object.keys(dark).sort());
});
