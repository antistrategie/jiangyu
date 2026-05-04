import { useState, useEffect, useMemo, useRef } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import styles from "../TemplateVisualEditor.module.css";

// Row height for the virtualised suggestion dropdown — matches
// `.fieldAdderItem`'s vertical box (space-1 padding × 2 + a single line of
// monospace text). Kept in sync with the CSS by visual inspection only;
// estimateSize is just a hint, the actual layout is content-driven once
// rendered.
export const SUGGESTION_ROW_HEIGHT = 28;

export interface SuggestionItem {
  readonly label: string;
  readonly tag?: string;
}

export interface SuggestionComboboxProps {
  value: string;
  placeholder: string;
  fetchSuggestions: () => Promise<readonly (string | SuggestionItem)[]>;
  onChange: (value: string) => void;
  /**
   * Fires only when the user explicitly picks a suggestion (click in the
   * dropdown, or Enter while the filtered list is non-empty). Use this for
   * "must pick from list" callers where typed text shouldn't commit until
   * the user confirms a real entry. `onChange` still fires on every
   * keystroke for the input-text mirror.
   */
  onCommit?: (value: string) => void;
  className?: string;
}

export function SuggestionCombobox({
  value,
  placeholder,
  fetchSuggestions,
  onChange,
  onCommit,
  className,
}: SuggestionComboboxProps) {
  const [open, setOpen] = useState(false);
  const [items, setItems] = useState<readonly SuggestionItem[]>([]);
  const [loaded, setLoaded] = useState(false);
  const wrapRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  // Reset cache when the fetch function changes (e.g. refType changed).
  // React-docs prev-state pattern: synchronous setState in render bails out
  // unless the prop changed, so this doesn't loop. The lint rule is named
  // `set-state-in-effect` but currently misfires on this conditional pattern.
  // https://react.dev/reference/react/useState#storing-information-from-previous-renders
  const [prevFetchSuggestions, setPrevFetchSuggestions] = useState(() => fetchSuggestions);
  if (prevFetchSuggestions !== fetchSuggestions) {
    /* eslint-disable @eslint-react/set-state-in-effect -- prev-state pattern in render, not in an effect; see comment above. */
    setPrevFetchSuggestions(() => fetchSuggestions);
    setLoaded(false);
    setItems([]);
    /* eslint-enable @eslint-react/set-state-in-effect */
  }

  useEffect(() => {
    if (!open || loaded) return;
    void fetchSuggestions()
      .then((result) => {
        setItems(result.map((r) => (typeof r === "string" ? { label: r } : r)));
        setLoaded(true);
      })
      .catch(() => setLoaded(true));
  }, [open, loaded, fetchSuggestions]);

  // Close on outside click
  useEffect(() => {
    if (!open) return;
    const handler = (e: MouseEvent) => {
      if (wrapRef.current && !wrapRef.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, [open]);

  const filtered = useMemo(() => {
    const lowerQuery = value.toLowerCase();
    return items.filter((item) => item.label.toLowerCase().includes(lowerQuery));
  }, [items, value]);

  // Virtualise the dropdown so the user gets all matches at every scale —
  // template-instance lists in MENACE run into the thousands. Each row is a
  // single text+badge button at ~28px tall (matches `.fieldAdderItem`
  // padding + a single line of mono text).
  const dropdownRef = useRef<HTMLDivElement>(null);
  // eslint-disable-next-line react-hooks/incompatible-library -- TanStack Virtual returns non-memoisable functions; the only API the library exposes.
  const rowVirtualizer = useVirtualizer({
    count: filtered.length,
    getScrollElement: () => dropdownRef.current,
    estimateSize: () => SUGGESTION_ROW_HEIGHT,
    overscan: 8,
  });

  return (
    <div
      className={`${styles.refCombobox} ${className ?? ""}`}
      ref={wrapRef}
      role="presentation"
      onClick={(e) => e.stopPropagation()}
    >
      <input
        ref={inputRef}
        type="text"
        className={styles.setValueInput}
        value={value}
        placeholder={placeholder}
        onFocus={() => setOpen(true)}
        onChange={(e) => {
          onChange(e.target.value);
          setOpen(true);
        }}
        onKeyDown={(e) => {
          if (e.key === "Escape") {
            setOpen(false);
            inputRef.current?.blur();
          } else if (e.key === "Enter" && filtered.length > 0) {
            const first = filtered[0];
            if (first) {
              onChange(first.label);
              onCommit?.(first.label);
              setOpen(false);
            }
          }
        }}
      />
      {open && loaded && filtered.length > 0 && (
        <div className={styles.refComboboxDropdown} ref={dropdownRef}>
          <div
            style={{
              height: `${rowVirtualizer.getTotalSize()}px`,
              position: "relative",
              width: "100%",
            }}
          >
            {rowVirtualizer.getVirtualItems().map((row) => {
              const item = filtered[row.index];
              if (!item) return null;
              return (
                <button
                  key={`${item.label}${item.tag ?? ""}`}
                  type="button"
                  className={`${styles.fieldAdderItem} ${styles.refComboboxRow} ${item.label === value ? styles.setOpMenuItemActive : ""}`}
                  style={{ transform: `translateY(${row.start}px)` }}
                  onClick={() => {
                    onChange(item.label);
                    onCommit?.(item.label);
                    setOpen(false);
                  }}
                >
                  <span className={styles.fieldAdderItemName}>{item.label}</span>
                  {item.tag && <span className={styles.suggestionTag}>{item.tag}</span>}
                </button>
              );
            })}
          </div>
        </div>
      )}
      {open && loaded && filtered.length === 0 && value.length > 0 && (
        <div className={styles.refComboboxDropdown}>
          <div className={styles.fieldAdderHint}>No matches</div>
        </div>
      )}
    </div>
  );
}
