/**
 * Compute where to insert a dropped tab based on the cursor x-coordinate.
 *
 * The tab bar container is the element wrapping the tab buttons (rendered by
 * TabbedMonacoEditor as `styles.tabScroll`). Each tab button carries a
 * `data-tab-path` attribute so we can identify them without coupling to the
 * CSS-Modules-hashed class names. The return value is an index into the
 * tabs array (0 = before the first tab, tabs.length = after the last) —
 * `layout.reorderTab` handles the post-removal index shift.
 */
export function computeTabDropIndex(container: Element, clientX: number): number {
  const tabEls = container.querySelectorAll<HTMLElement>("[data-tab-path]");
  for (const [i, el] of tabEls.entries()) {
    const rect = el.getBoundingClientRect();
    if (clientX < rect.left + rect.width / 2) return i;
  }
  return tabEls.length;
}
