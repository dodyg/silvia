// Appearance: the theme, the two text sizes, and the three font slots, applied as CSS variables.
// Font names only ever reach the page through setProperty with a quoted value, never as a stylesheet.

export type ThemeChoice = "system" | "light" | "dark";
export type LineHeightChoice = "compact" | "comfortable" | "spacious";
export type ReadingWidthChoice = "narrow" | "wide" | "full";

export interface Appearance {
  theme: ThemeChoice;
  interfaceSize: number;
  readingSize: number;
  lineHeight: LineHeightChoice;
  readingWidth: ReadingWidthChoice;
  interfaceFont: string;
  readingFont: string;
  monoFont: string;
}

export const SYSTEM_FONT = "System default";
export const INTERFACE_SIZE = { min: 11, max: 20, step: 1 } as const;
export const READING_SIZE = { min: 12, max: 28, step: 1 } as const;

const STORAGE_KEY = "silvia.appearance.v1";

const LINE_HEIGHTS: Record<LineHeightChoice, string> = { compact: "1.35", comfortable: "1.55", spacious: "1.8" };
const READING_WIDTHS: Record<ReadingWidthChoice, string> = { narrow: "56ch", wide: "72ch", full: "100%" };

const UI_FALLBACKS = ["Segoe UI", "Noto Sans Arabic", "system-ui", "sans-serif"];
const READING_FALLBACKS = ["Noto Naskh Arabic", "Segoe UI", "system-ui", "sans-serif"];
const MONO_FALLBACKS = ["Consolas", "monospace"];

/** Wraps a family name in CSS quotes, escaping the characters that could end the string. */
export function cssQuote(name: string): string {
  let quoted = '"';
  for (const ch of name) {
    if (ch === '"' || ch === "\\") {
      quoted += "\\" + ch;
    } else if (ch === "\n" || ch === "\r") {
      quoted += " ";
    } else {
      quoted += ch;
    }
  }

  return quoted + '"';
}

/** A font stack: the chosen family first, then its fallbacks. The system default uses only fallbacks. */
export function fontStack(choice: string, fallbacks: readonly string[]): string {
  const families = choice === SYSTEM_FONT ? [...fallbacks] : [choice, ...fallbacks];
  return families.map((family) => (family.includes(" ") || /^\d/.test(family) ? cssQuote(family) : family)).join(", ");
}

export function uiFontStack(choice: string): string {
  return fontStack(choice, UI_FALLBACKS);
}

export function readingFontStack(choice: string, uiChoice: string): string {
  return choice === SYSTEM_FONT ? uiFontStack(uiChoice) : fontStack(choice, READING_FALLBACKS);
}

export function monoFontStack(choice: string): string {
  return fontStack(choice === SYSTEM_FONT ? "Cascadia Mono" : choice, MONO_FALLBACKS);
}

/** Applies an appearance to the document. The theme follows the system unless it is set explicitly. */
export function applyAppearance(appearance: Appearance): void {
  const root = document.documentElement;
  if (appearance.theme === "system") {
    root.removeAttribute("data-theme");
  } else {
    root.setAttribute("data-theme", appearance.theme);
  }

  root.style.setProperty("--font-size-ui", `${appearance.interfaceSize}px`);
  root.style.setProperty("--font-size-reading", `${appearance.readingSize}px`);
  root.style.setProperty("--line-height", LINE_HEIGHTS[appearance.lineHeight]);
  root.style.setProperty("--reading-width", READING_WIDTHS[appearance.readingWidth]);
  root.style.setProperty("--font-ui", uiFontStack(appearance.interfaceFont));
  root.style.setProperty("--font-reading", readingFontStack(appearance.readingFont, appearance.interfaceFont));
  root.style.setProperty("--font-mono", monoFontStack(appearance.monoFont));
  remember(appearance);
}

/** Keeps a copy in local storage so the next launch paints the right colors before the host answers. */
function remember(appearance: Appearance): void {
  try {
    localStorage.setItem(
      STORAGE_KEY,
      JSON.stringify({
        theme: appearance.theme,
        uiSize: appearance.interfaceSize,
        readingSize: appearance.readingSize,
        fontUi: uiFontStack(appearance.interfaceFont),
        fontReading: readingFontStack(appearance.readingFont, appearance.interfaceFont),
        fontMono: monoFontStack(appearance.monoFont),
      }),
    );
  } catch {
    // Storage can be blocked. The host copy is authoritative; this is only a first-paint convenience.
  }
}

/** Reading size steps for Ctrl +, Ctrl - and Ctrl 0. */
export function stepReadingSize(current: number, direction: 1 | -1 | 0): number {
  if (direction === 0) {
    return 16;
  }

  return Math.min(READING_SIZE.max, Math.max(READING_SIZE.min, current + direction * READING_SIZE.step));
}
