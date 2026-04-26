# Jiangyu Design System

The Studio UI follows the Jiangyu Design System — an ink-wash × near-future tactical visual language inspired by East Asian calligraphy and the source material's character-sheet art.

## Palette

Five families. Tokens in `src/Jiangyu.Studio.UI/src/styles/tokens.css`.

- **Ink** — sumi neutrals
- **Paper** — warm parchment, never pure white
- **Cinnabar** — 朱 red, ≤10% of any surface
- **Gold** — decorative eyebrows on dark panels only
- **Jade** — informational/verified states

## Typography

Six semantic roles in `tokens.css`. Do not mix them up.

- `--font-display-cjk` Noto Serif SC: CJK display / hero glyphs (绛雨), big stat readouts (weight 900)
- `--font-display` Cormorant SC: chiseled western display serif — headings and **primary (filled) buttons** only
- `--font-label` Barlow Condensed: tracked uppercase labels, section eyebrows, modal headers, ghost/default buttons. Never body copy.
- `--font-ui` Noto Sans SC: CJK-capable body sans — body text, form inputs, data rows, banners
- `--font-editorial` Cormorant Garamond: long-form serif passages (About blurbs, credits notes)
- `--font-mono` JetBrains Mono: code, paths, hashes, CLI output, version stamps, small data values

Western labels are ALL CAPS with `--tracking-wider` / `--tracking-section`. Chinese headings are never tracked. The serif on primary buttons is intentional — it signals the weight of a committing action versus the throwaway feel of a ghost button.

## Surfaces

- **Corner radii**: `0` everywhere. Jiangyu is hard-edged.
- **Borders**: hairline-first. 1px default, 2px for emphasis. Double keyline (nested 1px with 4px gap) for hero frames only.
- **Shadows**: essentially none. Depth comes from hairline borders and paper-vs-ink contrast.
- **Animation**: minimal. Fades only, 80–120ms, `ease-out`. No bounces, springs, or parallax. Hover = instant colour swap. Press = 1px inset shadow (no scale).
- **Iconography**: hairline SVG icons, 24px grid, `stroke-width: 1.25`. No icon fonts, no emoji, no PNG icons.
- **Imagery tone**: warm, painted, hand-rendered. Grain preserved. Never cold, never purple, never gradients.

## Voice

Terse, disciplined, bilingual (Chinese leads, English supports). Dossier voice (declarative, clipped) is primary; character voice (first-person to 长官) is accent only.

## Form controls

Checkboxes and radios are custom-styled globally in `global.css`. Ink borders on paper background, cinnabar fill/dot when active. No browser chrome.

## Modal dossier pattern

Long-running / state-rich actions (e.g. Compile) use a two-column modal at `min(1100px, 92vw) × min(760px, 88vh)`:

- **Left** column: terminal-style log on `--bg-inverse` with mono text, gold eyebrow, ink-0 scrollbar track.
- **Right** column: paper-toned info panel with 2×2 stat grid (Noto Serif SC 900 numbers, Barlow Condensed eyebrows), sub-stat rows, action buttons at the bottom.

`CompileModal` and `SettingsModal` are the canonical references; new modals should align to this shape. Long action completions also push a toast via `useToast()` with duration / warning count as detail and a Reveal action when a file artefact exists.

## Stickers and toasts

Character stickers live at `src/Jiangyu.Studio.UI/public/stickers/Jiangyu_001.jpg`…`_009.jpg`. Mood pools in `lib/toast/stickers.ts`:

- Success: 004 / 007 / 009
- Error: 001 / 003 / 006 / 008
- Info: 002 / 005

Toasts render fixed bottom-centre via `ToastContainer` with `aria-live="polite"` (errors `role="alert"`, others `role="status"`). 8s auto-dismiss, mood-matched sticker per variant, optional action buttons (e.g. "Reveal" for exported files).

## Confirm dialog

Destructive confirmations use `<ConfirmDialog>` (`components/ConfirmDialog/`), not `window.confirm`. Portal-based modal with Escape/Enter shortcuts and a `danger` variant for delete flows. Toasts are non-blocking and the wrong surface for "are you sure?" prompts.
