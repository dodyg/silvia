import { test } from "node:test";
import assert from "node:assert/strict";
import { cssQuote, fontStack, monoFontStack, readingFontStack, stepReadingSize, uiFontStack } from "./theme";

test("font names are quoted and cannot end the CSS string", () => {
  assert.equal(cssQuote('Evil"); --x: red'), '"Evil\\"); --x: red"');
  assert.equal(cssQuote("back\\slash"), '"back\\\\slash"');
});

test("the chosen family comes first, then its fallbacks", () => {
  assert.equal(fontStack("Source Serif 4", ["Georgia", "serif"]), '"Source Serif 4", Georgia, serif');
});

test("the system default uses only the fallback stack", () => {
  assert.equal(uiFontStack("System default"), '"Segoe UI", "Noto Sans Arabic", system-ui, sans-serif');
  assert.equal(readingFontStack("System default", "Inter"), uiFontStack("Inter"));
  assert.equal(monoFontStack("System default"), '"Cascadia Mono", Consolas, monospace');
});

test("reading size steps stay inside the allowed range", () => {
  assert.equal(stepReadingSize(16, 1), 17);
  assert.equal(stepReadingSize(28, 1), 28);
  assert.equal(stepReadingSize(12, -1), 12);
  assert.equal(stepReadingSize(20, 0), 16);
});
