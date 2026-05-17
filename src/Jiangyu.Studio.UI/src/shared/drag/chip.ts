// Custom drag image for HTML5 drag-and-drop. Browsers auto-snapshot the
// dragged element as the floating ghost, but our pane-drag handles are empty
// strips (tabFill, BrowserDragBar) so the default ghost is invisible — and
// even tab-button ghosts render poorly under WebKitGTK. A built-to-match-the
// design-system chip keeps feedback consistent across every drag source.
//
// The chip is appended off-screen, handed to setDragImage (the browser
// rasterises it synchronously), then scheduled for removal on the next tick.

export function attachDragChip(event: React.DragEvent | DragEvent, label: string): void {
  const dt = event.dataTransfer;
  if (dt === null) return;

  const chip = document.createElement("div");
  chip.textContent = label;
  // Inline styles keep the helper self-contained — design tokens come in via
  // CSS custom properties so the chip follows theme changes like everything
  // else. Hard-edged, hairline border, no shadow — matches the design system.
  chip.style.cssText = [
    "position: fixed",
    "top: -1000px",
    "left: -1000px",
    "padding: 4px 10px",
    "background: var(--paper-1, #f5efdf)",
    "color: var(--fg, #1a1a1a)",
    "border: 1px solid var(--rule-strong, #1a1a1a)",
    "font-family: var(--font-mono, ui-monospace, monospace)",
    "font-size: 12px",
    "line-height: 1.4",
    "white-space: nowrap",
    "pointer-events: none",
    "user-select: none",
    "z-index: 2147483647",
  ].join(";");
  document.body.appendChild(chip);

  // setDragImage(el, x, y) anchors the cursor at position (x, y) INSIDE the
  // image. Negative offsets put the cursor outside the image's top-left, so
  // the chip renders below-and-right of the cursor with a small gap. Positive
  // offsets would either cover the label (small values) or push the chip
  // up-and-left (values past width/height), which feels wrong.
  dt.setDragImage(chip, -12, -12);

  // The browser has already rasterised the chip by the time this callback
  // runs; removing it immediately (same tick) would yank the snapshot. A
  // zero-delay timeout defers cleanup until after the drag has started.
  setTimeout(() => chip.remove(), 0);
}
