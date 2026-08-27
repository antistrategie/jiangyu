# Jiangyu Design System

The Studio UI follows the Jiangyu Design System — an ink-wash × near-future tactical visual language inspired by East Asian calligraphy and the source material's character-sheet art.

## Palette

Five families. Tokens in `src/Jiangyu.Studio.UI/src/styles/tokens.css`.

- **Ink** — sumi neutrals
- **Paper** — warm parchment, never pure white
- **Cinnabar** — 朱 red, ≤10% of any surface; carries primary committing actions and danger
- **Gold** — decorative eyebrows on dark panels only
- **Jade** — informational/verified states

### Two ramps, two roles

Ink and paper are ramps, not colours. `--paper-*` is the surface ramp (paper-1 the base,
paper-0 the raised panel, paper-2 the hover and well fill, paper-3 the strongest row
selection). `--ink-*` is the mark ramp (ink-0 the strongest, ink-4 the faintest). Everything
else follows: `--bg*`, `--fg*` and `--rule*` alias the two ramps, and the dark theme swaps the
ramps under `[data-theme="dark"]`.

Painting a surface from the ink ramp or a mark from the paper ramp reads correctly in light
and inverts into illegibility in dark, so the semantic tokens carry the places that want the
other ramp:

- `--bg-inverse` / `--bg-inverse-2`: ink chrome, meaning the topbar, status bar, compile log
  and filled ink controls. Dark sinks this below the workspace so the frame still reads as a
  frame.
- `--fg-on-inverse`, `--fg-on-inverse-muted`, `--fg-on-inverse-subtle`, `--rule-on-inverse`:
  marks and seams on that chrome.
- `--fg-on-accent`: ink on a cinnabar fill. Cinnabar carries its own contrast, so this is the
  same colour in both themes.
- `--accent-fill` and `--bg-status-running`: cinnabar and jade in their fill role. On paper one
  colour serves both roles, on ink they pull apart, because a fill sitting under near-white ink
  has to be deep while a mark on a dark surface has to be bright. `--cinnabar-*` and `--jade-*`
  stay marks, so never paint a fill from them.
- `--fg-on-accent-weak`: the deep mark that sits on `--accent-weak`, which is a light fill in
  dark.

`src/styles/tokens.test.ts` enforces three things: no component stylesheet paints across the
ramps, every literal colour in `:root` is restated in the dark block, and every foreground and
surface pairing clears its contrast floor in both themes. Add a pair to that list whenever a
token starts carrying text.

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
- **Theme**: light and dark, chosen in Settings · Appearance and persisted as `theme` in
  `studio.json`. The boot script in `index.html` stamps `data-theme` before first paint, and a
  switch rebuilds the Monaco theme from the resolved tokens. Torn-out pane windows are separate
  documents, so they follow through the localStorage mirror's `storage` event rather than the
  in-document pub-sub.
- **Animation**: minimal. Fades only, 80–120ms, `ease-out`. No bounces, springs, or parallax. Hover = instant colour swap. Press = 1px inset shadow (no scale).
- **Iconography**: hairline SVG icons, 24px grid, `stroke-width: 1.25`. No icon fonts, no emoji, no PNG icons.
- **Imagery tone**: warm, painted, hand-rendered. Grain preserved. Never cold, never purple, never gradients.

## Voice

Terse, disciplined, bilingual (Chinese leads, English supports). Dossier voice (declarative, clipped) is primary; character voice (first-person to 长官) is accent only.

## Buttons

Three weights, picked by what the button does, not by where it sits.

- **Primary (filled cinnabar)** — committing action that produces or persists something: Open Project, New Project, Export, Compile, Install, ConfirmDialog primary, ConfirmDialog danger. Cinnabar fill, paper text, Cormorant SC serif (the serif signals weight, per the typography rules above). Hover applies `filter: brightness(1.1)`. Same treatment for confirm and danger; the verb on the button label distinguishes them.
- **Ghost (hairline)** — secondary action, or the inverse state of a primary (e.g. Remove next to Install). Transparent fill, hairline border, Barlow Condensed tracked label or default ink text. Hover swaps to `--bg-sunken`. Use this when there are two actions in a row and the other one is the primary.
- **Quiet (no border)** — tertiary or in-context action: link-style buttons in headers, sidebar entries, palette items. No border, label/UI font, ink-2 or muted text. Hover often only swaps colour.

The cinnabar-fill rule is what keeps the page from drifting toward "everything looks the same" — primary actions are where the user makes the page change, and they're the only place red appears at any size. The ≤10%-of-any-surface limit on cinnabar still applies; if a panel ends up with more than one cinnabar-filled button visible at once, demote the secondary one to ghost.

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
