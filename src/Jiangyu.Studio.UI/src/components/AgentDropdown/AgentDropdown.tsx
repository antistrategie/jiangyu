import { useEffect, useRef, useState, type ReactNode } from "react";
import type { InstalledAgent } from "@lib/rpc";
import {
  MenuList,
  MenuListBody,
  MenuItem,
  MenuItemIcon,
  MenuItemLabel,
  MenuItemMeta,
  MenuFooter,
} from "@components/MenuList/MenuList";
import styles from "./AgentDropdown.module.css";

interface AgentDropdownProps {
  readonly agents: readonly InstalledAgent[];
  readonly onSelect: (agent: InstalledAgent) => void;
  readonly onOpenRegistry: () => void;
  /**
   * Trigger button content. The wrapper handles the button element and the
   * open/close state; callers supply the inner label/icon and a class for
   * the surrounding chrome via `triggerClassName`.
   */
  readonly triggerContent: ReactNode;
  readonly triggerClassName?: string;
  readonly triggerAriaLabel?: string;
  /** Anchor the menu to the right edge of the trigger (header use). */
  readonly align?: "left" | "right";
  readonly disabled?: boolean;
}

export function AgentDropdown({
  agents,
  onSelect,
  onOpenRegistry,
  triggerContent,
  triggerClassName,
  triggerAriaLabel,
  align = "left",
  disabled = false,
}: AgentDropdownProps) {
  const [open, setOpen] = useState(false);
  const wrapRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const onMouseDown = (e: MouseEvent) => {
      if (wrapRef.current?.contains(e.target as Node) !== true) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") setOpen(false);
    };
    document.addEventListener("mousedown", onMouseDown);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onMouseDown);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  return (
    <div className={styles.wrap} ref={wrapRef}>
      <button
        type="button"
        className={triggerClassName}
        aria-label={triggerAriaLabel}
        aria-haspopup="menu"
        aria-expanded={open}
        disabled={disabled}
        onClick={() => setOpen((o) => !o)}
      >
        {triggerContent}
      </button>
      {open && (
        <AgentMenu
          agents={agents}
          align={align}
          onSelect={(a) => {
            setOpen(false);
            onSelect(a);
          }}
          onOpenRegistry={() => {
            setOpen(false);
            onOpenRegistry();
          }}
        />
      )}
    </div>
  );
}

interface AgentMenuProps {
  readonly agents: readonly InstalledAgent[];
  readonly onSelect: (agent: InstalledAgent) => void;
  readonly onOpenRegistry: () => void;
  readonly align?: "left" | "right";
  readonly inline?: boolean;
}

/**
 * Renders the menu body — agent rows + "Add more agents…" footer. Use as a
 * popover (default; positioned via `.popover` + align) or inline (when
 * the menu IS the empty-state UI rather than a hidden dropdown).
 */
export function AgentMenu({
  agents,
  onSelect,
  onOpenRegistry,
  align = "left",
  inline = false,
}: AgentMenuProps) {
  const positionalClass = inline
    ? styles.inline
    : align === "right"
      ? styles.popover_right
      : styles.popover_left;

  return (
    <MenuList className={positionalClass}>
      {agents.length > 0 && (
        <MenuListBody>
          {agents.map((agent) => (
            <MenuItem key={agent.id} onClick={() => onSelect(agent)}>
              <MenuItemIcon src={agent.iconUrl} />
              <MenuItemLabel>{agent.name}</MenuItemLabel>
              {agent.version !== null && agent.version !== undefined && (
                <MenuItemMeta>v{agent.version}</MenuItemMeta>
              )}
            </MenuItem>
          ))}
        </MenuListBody>
      )}
      <MenuFooter onClick={onOpenRegistry}>Add more agents…</MenuFooter>
    </MenuList>
  );
}
