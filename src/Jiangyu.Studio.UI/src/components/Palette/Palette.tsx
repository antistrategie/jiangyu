import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";
import type { PaletteAction } from "../../lib/actions.tsx";
import { buildFuse, filterActions, groupByScope } from "./paletteFilter.ts";
import styles from "./Palette.module.css";

interface PaletteProps {
  readonly open: boolean;
  readonly onClose: () => void;
  readonly actions: readonly PaletteAction[];
}

export function Palette({ open, onClose, actions }: PaletteProps) {
  if (!open) return null;
  return <PaletteDialog onClose={onClose} actions={actions} />;
}

interface PaletteDialogProps {
  readonly onClose: () => void;
  readonly actions: readonly PaletteAction[];
}

function PaletteDialog({ onClose, actions }: PaletteDialogProps) {
  const [query, setQuery] = useState("");
  const [selected, setSelected] = useState(0);
  const inputRef = useRef<HTMLInputElement | null>(null);
  const resultsRef = useRef<HTMLDivElement | null>(null);

  // Fuse is only built while the dialog is mounted; with 10k+ entries this
  // index is expensive and pointless to maintain when the palette is closed.
  const fuse = useMemo(() => buildFuse(actions), [actions]);
  const filtered = useMemo(() => filterActions(query, actions, fuse), [query, actions, fuse]);
  const groups = useMemo(() => groupByScope(filtered), [filtered]);
  const indexOf = useMemo(() => {
    const m = new Map<PaletteAction, number>();
    filtered.forEach((item, i) => m.set(item, i));
    return m;
  }, [filtered]);

  useEffect(() => {
    const id = requestAnimationFrame(() => inputRef.current?.focus());
    return () => cancelAnimationFrame(id);
  }, []);

  useEffect(() => {
    setSelected(0);
  }, [query]);

  useLayoutEffect(() => {
    const container = resultsRef.current;
    if (!container) return;
    const target = container.querySelector<HTMLElement>(`[data-idx="${selected}"]`);
    target?.scrollIntoView({ block: "nearest" });
  }, [selected]);

  const run = (action: PaletteAction | undefined) => {
    if (!action) return;
    onClose();
    void action.run();
  };

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === "Escape") {
      e.preventDefault();
      onClose();
    } else if (e.key === "ArrowDown") {
      e.preventDefault();
      setSelected((s) => (filtered.length === 0 ? 0 : Math.min(filtered.length - 1, s + 1)));
    } else if (e.key === "ArrowUp") {
      e.preventDefault();
      setSelected((s) => Math.max(0, s - 1));
    } else if (e.key === "Enter") {
      e.preventDefault();
      run(filtered[selected]);
    }
  };

  return createPortal(
    <div
      className={styles.backdrop}
      onMouseDown={(e) => {
        if (e.target === e.currentTarget) onClose();
      }}
    >
      <div className={styles.dialog} onKeyDown={onKeyDown} role="dialog" aria-modal="true">
        <div className={styles.header}>
          <span className={styles.headerLabel}>Command · 命令</span>
          <input
            ref={inputRef}
            className={styles.input}
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="Type to filter commands or files…"
          />
          <span className={styles.footerKbd}>esc</span>
        </div>
        <div className={styles.results} ref={resultsRef}>
          {groups.map(([scope, list]) => (
            <div key={scope}>
              <div className={styles.groupLabel}>{scope}</div>
              {list.map((item) => {
                const myIdx = indexOf.get(item) ?? -1;
                const active = myIdx === selected;
                return (
                  <button
                    key={item.id}
                    data-idx={myIdx}
                    type="button"
                    className={`${styles.row} ${active ? styles.rowActive : ""}`}
                    onMouseEnter={() => setSelected(myIdx)}
                    onClick={() => run(item)}
                  >
                    {item.cn !== undefined && <span className={styles.rowCjk}>{item.cn}</span>}
                    <span className={styles.rowLabel}>{item.label}</span>
                    {item.desc !== undefined && <span className={styles.rowDesc}>{item.desc}</span>}
                    {item.kbd !== undefined && <span className={styles.rowKbd}>{item.kbd}</span>}
                  </button>
                );
              })}
            </div>
          ))}
          {filtered.length === 0 && (
            <div className={styles.empty}>No commands match &ldquo;{query}&rdquo;</div>
          )}
        </div>
        <div className={styles.footer}>
          <span className={styles.footerHint}>
            <span className={styles.footerKbd}>↑↓</span>navigate
          </span>
          <span className={styles.footerHint}>
            <span className={styles.footerKbd}>↵</span>run
          </span>
          <span className={styles.footerHint}>
            <span className={styles.footerKbd}>esc</span>close
          </span>
          <span className={styles.footerCount}>
            {filtered.length} result{filtered.length === 1 ? "" : "s"}
          </span>
        </div>
      </div>
    </div>,
    document.body,
  );
}
