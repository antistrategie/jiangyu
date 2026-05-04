import type { ReactNode } from "react";
import styles from "./MenuList.module.css";

/**
 * Shared menu list chrome: a vertical list of selectable rows with an
 * optional accent footer for "add new" / "open more" actions. Used both
 * inline (e.g. AgentPanel empty state, where the menu is the whole UI)
 * and inside a popover (e.g. AgentDropdown). Positioning and outside-click
 * behaviour are the caller's concern; this component is purely visual.
 */

interface MenuListProps {
  readonly children: ReactNode;
  readonly className?: string;
}

export function MenuList({ children, className }: MenuListProps) {
  const cls = className === undefined ? styles.menu : `${styles.menu} ${className}`;
  return (
    <div className={cls} role="menu">
      {children}
    </div>
  );
}

interface MenuListBodyProps {
  readonly children: ReactNode;
}

/** Scrollable container for menu items; pair with MenuFooter as a sibling. */
export function MenuListBody({ children }: MenuListBodyProps) {
  return <div className={styles.list}>{children}</div>;
}

interface MenuItemProps {
  readonly children: ReactNode;
  readonly onClick: () => void;
  readonly disabled?: boolean;
  /** When true, render as the currently-selected option in a single-pick
   *  list (e.g. the active mode in a session-options dropdown). */
  readonly active?: boolean;
}

export function MenuItem({ children, onClick, disabled, active }: MenuItemProps) {
  const cls = active === true ? `${styles.item} ${styles.itemActive}` : styles.item;
  return (
    <button
      type="button"
      role={active === undefined ? "menuitem" : "menuitemradio"}
      aria-checked={active}
      className={cls}
      onClick={onClick}
      disabled={disabled}
    >
      {children}
    </button>
  );
}

interface MenuItemIconProps {
  readonly src?: string | null | undefined;
  readonly alt?: string;
}

export function MenuItemIcon({ src, alt = "" }: MenuItemIconProps) {
  if (src === null || src === undefined) {
    return <span className={styles.itemIconPlaceholder} aria-hidden="true" />;
  }
  return <img src={src} alt={alt} className={styles.itemIcon} />;
}

interface MenuItemLabelProps {
  readonly children: ReactNode;
}

export function MenuItemLabel({ children }: MenuItemLabelProps) {
  return <span className={styles.itemName}>{children}</span>;
}

interface MenuItemMetaProps {
  readonly children: ReactNode;
}

export function MenuItemMeta({ children }: MenuItemMetaProps) {
  return <span className={styles.itemMeta}>{children}</span>;
}

interface MenuItemContentProps {
  readonly children: ReactNode;
}

/** Stacked label + subtext column for items where the second line is a
 *  subtitle rather than a right-aligned meta value. */
export function MenuItemContent({ children }: MenuItemContentProps) {
  return <div className={styles.itemContent}>{children}</div>;
}

interface MenuItemSubtextProps {
  readonly children: ReactNode;
}

export function MenuItemSubtext({ children }: MenuItemSubtextProps) {
  return <span className={styles.itemSubtext}>{children}</span>;
}

/** Hairline divider between groups of menu items. */
export function MenuSeparator() {
  return <div className={styles.separator} aria-hidden="true" />;
}

interface MenuFooterProps {
  readonly children: ReactNode;
  readonly onClick: () => void;
}

/**
 * Accent footer row for "add new" / "open registry" / "see all" actions.
 * Visually distinct (sunken bg, label-tracked uppercase) so it reads as a
 * separate affordance from the list above.
 */
export function MenuFooter({ children, onClick }: MenuFooterProps) {
  return (
    <button type="button" role="menuitem" className={styles.footer} onClick={onClick}>
      {children}
    </button>
  );
}
