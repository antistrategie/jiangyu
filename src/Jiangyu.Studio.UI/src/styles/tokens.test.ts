import { describe, expect, it } from "vitest";
import tokens from "./tokens.css?raw";

// Every component stylesheet, as source text. Vite resolves the glob, so the
// test needs no filesystem access and stays in the default Node environment.
const stylesheets: Record<string, string> = import.meta.glob("../**/*.module.css", {
  query: "?raw",
  import: "default",
  eager: true,
});

function block(selector: string): string {
  const start = tokens.indexOf(`${selector} {`);
  if (start === -1) throw new Error(`tokens.css has no ${selector} block`);
  return tokens.slice(start, tokens.indexOf("\n}", start));
}

function declaredTokens(selector: string): Map<string, string> {
  const found = new Map<string, string>();
  for (const match of block(selector).matchAll(/(--[a-z0-9-]+):\s*([^;]+);/g)) {
    const [, name, value] = match;
    if (name === undefined || value === undefined) continue;
    found.set(name, value.trim());
  }
  return found;
}

// Tokens that hold the same colour in both themes by intent rather than by
// omission: each names ink on a fill that carries its own contrast.
const THEME_INVARIANT = new Set(["--fg-on-accent", "--track-on-accent"]);

describe("theme tokens", () => {
  it("restates every literal colour from :root in the dark theme", () => {
    const light = declaredTokens(":root");
    const dark = declaredTokens('[data-theme="dark"]');

    const missing = [...light]
      .filter(([, value]) => /^(#|rgba?\()/.test(value))
      .filter(([name]) => !THEME_INVARIANT.has(name) && !dark.has(name))
      .map(([name]) => name);

    expect(missing).toEqual([]);
  });
});

// The dark theme works by swapping the two ramps: `--paper-*` is the surface
// ramp and `--ink-*` the mark ramp. A rule that paints a surface from the ink
// ramp, or a mark from the paper ramp, reads correctly in light and inverts
// into illegibility in dark. The semantic tokens (--bg-inverse, --fg-on-inverse,
// --fg-on-accent, --rule*) cover every place that genuinely wants the other
// ramp. Translucent washes are exempt: a color-mix towards transparent tracks
// whatever surface it sits on.
describe("ramp roles in component stylesheets", () => {
  const surfaceProperty = /^(background|background-color)$/;
  const markProperty = /^(color|border|border-[a-z-]+|outline|outline-color)$/;

  it("never paints a surface from the ink ramp or a mark from the paper ramp", () => {
    const offences: string[] = [];

    for (const [path, source] of Object.entries(stylesheets)) {
      source.split("\n").forEach((line, index) => {
        const match = /^\s*([a-z-]+):\s*(.+?);?\s*$/.exec(line);
        if (match === null) return;
        const property = match[1] ?? "";
        const value = match[2] ?? "";
        if (value.includes("color-mix(")) return;

        const paintsInk = surfaceProperty.test(property) && /var\(--ink-[0-9]\)/.test(value);
        const marksPaper = markProperty.test(property) && /var\(--paper-[0-9]\)/.test(value);
        if (paintsInk || marksPaper) {
          offences.push(`${path.replace("../", "")}:${index + 1}  ${line.trim()}`);
        }
      });
    }

    expect(offences).toEqual([]);
  });
});

// --- contrast -------------------------------------------------------------

function resolve(name: string, table: Map<string, string>): string {
  let value = table.get(name) ?? "";
  while (value.startsWith("var(")) {
    value = table.get(value.slice(4, value.indexOf(")"))) ?? "";
  }
  return value;
}

function relativeLuminance(hex: string): number {
  const channels = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16) / 255);
  const linear = channels.map((c) => (c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4));
  return 0.2126 * (linear[0] ?? 0) + 0.7152 * (linear[1] ?? 0) + 0.0722 * (linear[2] ?? 0);
}

function contrast(a: string, b: string): number {
  const [high, low] = [relativeLuminance(a), relativeLuminance(b)].sort((x, y) => y - x);
  return ((high ?? 0) + 0.05) / ((low ?? 0) + 0.05);
}

// Pairs that carry readable text. The floor is the 4.5:1 body-text ratio
// unless a pair names its own, which only the Monaco string colour does: gold
// on parchment measures 4.26:1 and is the light theme's established value, so
// it is pinned there to catch a regression rather than raised.
const TEXT_PAIRS: readonly (readonly [string, string, number?])[] = [
  ["--fg", "--bg"],
  ["--fg", "--bg-raised"],
  ["--fg-muted", "--bg"],
  ["--accent", "--bg"],
  ["--accent", "--bg-raised"],
  ["--accent", "--bg-sunken"],
  ["--accent-strong", "--bg-raised"],
  ["--jade-0", "--bg-raised"],
  ["--gold-deep", "--bg-raised"],
  ["--gold-0", "--bg-raised", 4.2],
  ["--fg-on-accent", "--accent-fill"],
  ["--fg-on-inverse", "--bg-inverse"],
  ["--fg-on-inverse-muted", "--bg-inverse"],
  ["--fg-on-inverse", "--bg-status-running"],
  ["--warning", "--bg-inverse"],
  ["--jade-2", "--bg-inverse"],
];

// Faint marks: quiet by design in both themes. The floor is here to catch a
// ramp collapsing onto its own surface, not to certify legibility.
const QUIET_PAIRS: readonly (readonly [string, string])[] = [
  ["--fg-subtle", "--bg"],
  ["--fg-muted", "--bg-sunken"],
  ["--fg-on-inverse-subtle", "--bg-inverse"],
  ["--fg-on-accent-weak", "--accent-weak"],
  ["--accent-weak", "--bg-inverse"],
  ["--selection-fg", "--selection-bg"],
  ["--success", "--bg"],
  ["--info", "--bg"],
];

describe("token contrast", () => {
  const themes: readonly (readonly [string, Map<string, string>])[] = [
    ["light", declaredTokens(":root")],
    ["dark", new Map([...declaredTokens(":root"), ...declaredTokens('[data-theme="dark"]')])],
  ];

  for (const [name, table] of themes) {
    it(`keeps text pairs above their contrast floor in ${name}`, () => {
      const failures = TEXT_PAIRS.map(([fg, bg, floor]) => {
        const ratio = contrast(resolve(fg, table), resolve(bg, table));
        return {
          pair: `${fg} on ${bg}`,
          ratio: Math.round(ratio * 100) / 100,
          floor: floor ?? 4.5,
        };
      }).filter((entry) => entry.ratio < entry.floor);

      expect(failures).toEqual([]);
    });

    it(`keeps faint marks off their own surface in ${name}`, () => {
      const failures = QUIET_PAIRS.map(([fg, bg]) => {
        const ratio = contrast(resolve(fg, table), resolve(bg, table));
        return { pair: `${fg} on ${bg}`, ratio: Math.round(ratio * 100) / 100 };
      }).filter((entry) => entry.ratio < 1.9);

      expect(failures).toEqual([]);
    });
  }
});
