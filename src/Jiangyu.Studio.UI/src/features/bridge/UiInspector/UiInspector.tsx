import {
  useCallback,
  useDeferredValue,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from "react";
import { ChevronDown, Copy, RadioTower, RefreshCw, Search } from "lucide-react";
import { Button } from "@shared/ui/Button/Button";
import { EmptyState } from "@shared/ui/EmptyState/EmptyState";
import { LoadingBanner } from "@shared/ui/LoadingBanner/LoadingBanner";
import { ContextMenu, type ContextMenuEntry } from "@shared/ui/ContextMenu/ContextMenu";
import { useToastPush } from "@shared/toast";
import { bridgeUiCapture, type UiDump, type UiNode } from "@features/bridge/bridge";
import { useBridgeStatus } from "@features/bridge/useBridgeStatus";
import { onKeyActivate } from "@shared/utils/a11y";
import { bestSelector, nodeMatches, selectorsOf, styleEntries, truncate } from "./helpers";
import styles from "./UiInspector.module.css";

interface Menu {
  readonly x: number;
  readonly y: number;
  readonly items: ContextMenuEntry[];
}

/** The paths to render when a search is active: nodes to show, and nodes that match themselves. */
interface MatchSet {
  readonly visible: ReadonlySet<string>;
  readonly matched: ReadonlySet<string>;
}

/**
 * Browses a live capture of the running game's UI tree (over the bridge) so a modder
 * can discover the selectors to target with the Game.UI inject API. Capture on demand,
 * search by type/name/class, and copy a node's selector. Read-only.
 */
export function UiInspector() {
  const push = useToastPush();
  const { status, setStatus } = useBridgeStatus();
  const [dump, setDump] = useState<UiDump | null>(null);
  const [capturing, setCapturing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [query, setQuery] = useState("");
  const [collapsed, setCollapsed] = useState<ReadonlySet<string>>(() => new Set());
  const [menu, setMenu] = useState<Menu | null>(null);
  const [selected, setSelected] = useState<{ path: string; node: UiNode } | null>(null);
  const treeRef = useRef<HTMLDivElement>(null);

  const connected = status?.connected ?? false;
  const enabled = status?.enabled ?? false;

  // Search re-filters the whole tree, so defer it to keep typing responsive on big trees.
  const deferredQuery = useDeferredValue(query);

  // One post-order pass per (dump, query): the set of paths to render and the ones that
  // match. Avoids re-walking each subtree once per ancestor during render. null = no filter.
  const filter = useMemo<MatchSet | null>(() => {
    if (dump === null) return null;
    const q = deferredQuery.trim();
    if (q === "") return null;
    const visible = new Set<string>();
    const matched = new Set<string>();
    const walk = (node: UiNode, path: string): boolean => {
      let descendantMatch = false;
      for (const [i, child] of (node.children ?? []).entries()) {
        if (walk(child, `${path}.${i}`)) descendantMatch = true;
      }
      const self = nodeMatches(node, q);
      if (self) matched.add(path);
      if (self || descendantMatch) {
        visible.add(path);
        return true;
      }
      return false;
    };
    if (dump.screenTree !== null) walk(dump.screenTree, "s");
    if (dump.dialogTree !== null) walk(dump.dialogTree, "d");
    return { visible, matched };
  }, [dump, deferredQuery]);

  const capture = useCallback(async () => {
    setCapturing(true);
    setError(null);
    try {
      setDump(await bridgeUiCapture());
      // The new tree has fresh node objects, so the old selection no longer points at one.
      setSelected(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
      // A failed request tears down the socket, so reflect the disconnect at once
      // rather than leaving the controls live until the next poll.
      setStatus((prev) => (prev ? { ...prev, connected: false } : prev));
    } finally {
      setCapturing(false);
    }
  }, [setStatus]);

  const copy = useCallback(
    (snippet: string) => {
      void navigator.clipboard.writeText(snippet);
      push({ variant: "success", message: `Copied ${snippet}` });
    },
    [push],
  );

  const toggle = useCallback((path: string) => {
    setCollapsed((prev) => {
      const next = new Set(prev);
      if (next.has(path)) next.delete(path);
      else next.add(path);
      return next;
    });
  }, []);

  const openMenu = useCallback(
    (x: number, y: number, node: UiNode) => {
      const items = selectorsOf(node).map(
        (s): ContextMenuEntry => ({ label: `Copy ${s}`, onSelect: () => copy(s) }),
      );
      if (items.length > 0) setMenu({ x, y, items });
    },
    [copy],
  );

  // Rebuild the tree only when the capture, filter, or collapse state changes — not on
  // unrelated re-renders (the status poll, opening the context menu), which matters at scale.
  const tree = useMemo<ReactNode>(() => {
    if (dump === null) return null;

    const renderNode = (node: UiNode, path: string, depth: number): ReactNode => {
      if (filter !== null && !filter.visible.has(path)) return null;

      const children = node.children ?? [];
      const hasChildren =
        filter !== null
          ? children.some((_, i) => filter.visible.has(`${path}.${i}`))
          : children.length > 0;
      const isCollapsed = filter === null && collapsed.has(path);
      const isMatch = filter?.matched.has(path) ?? false;
      const best = bestSelector(node);

      return (
        <div key={path}>
          <div
            className={`${styles.row} ${isMatch ? styles.match : ""}`}
            data-path={path}
            role="button"
            tabIndex={0}
            style={{ paddingLeft: depth * 14 + 8 }}
            onClick={() => setSelected({ path, node })}
            onKeyDown={onKeyActivate(() => setSelected({ path, node }))}
            onContextMenu={(e) => {
              e.preventDefault();
              openMenu(e.clientX, e.clientY, node);
            }}
          >
            {hasChildren && filter === null ? (
              <button
                type="button"
                className={`${styles.expander} ${isCollapsed ? "" : styles.expanderOpen}`}
                onClick={(e) => {
                  e.stopPropagation();
                  toggle(path);
                }}
                aria-label={isCollapsed ? "Expand" : "Collapse"}
              >
                <ChevronDown size={12} />
              </button>
            ) : (
              <span className={styles.expanderSpacer} />
            )}
            <span className={styles.label}>
              <span className={styles.type}>{node.type ?? "?"}</span>
              {node.name !== null && node.name !== "" && (
                <span className={styles.name}>#{node.name}</span>
              )}
              {[...new Set(node.classes ?? [])].map((c) => (
                <span key={c} className={styles.cls}>
                  .{c}
                </span>
              ))}
              {node.text !== null && node.text !== "" && (
                <span className={styles.text}>&ldquo;{truncate(node.text)}&rdquo;</span>
              )}
            </span>
            {best !== null && (
              <button
                className={styles.copyBtn}
                title={`Copy ${best}`}
                aria-label="Copy selector"
                onClick={(e) => {
                  e.stopPropagation();
                  copy(best);
                }}
              >
                <Copy size={11} />
              </button>
            )}
          </div>
          {hasChildren &&
            !isCollapsed &&
            children.map((c, i) => renderNode(c, `${path}.${i}`, depth + 1))}
        </div>
      );
    };

    return (
      <div className={styles.body}>
        <div className={styles.meta}>
          <span>{dump.activeScreen ?? "(no screen)"}</span>
          {dump.currentDialog !== null && <span>dialog: {dump.currentDialog}</span>}
          <span>{dump.nodeCount} nodes</span>
          {dump.truncated && <span className={styles.truncated}>truncated</span>}
        </div>
        <div className={styles.tree} ref={treeRef}>
          {dump.screenTree !== null && renderNode(dump.screenTree, "s", 0)}
          {dump.dialogTree !== null && (
            <>
              <div className={styles.section}>Dialog</div>
              {renderNode(dump.dialogTree, "d", 0)}
            </>
          )}
          {dump.screenTree === null && dump.dialogTree === null && (
            <div className={styles.section}>No UI tree captured.</div>
          )}
        </div>
      </div>
    );
  }, [dump, filter, collapsed, copy, toggle, openMenu]);

  // Highlight the selected row by toggling a class on the matching DOM node, instead of
  // rebuilding the whole tree memo on every selection. useLayoutEffect re-applies before
  // paint after any tree rebuild (collapse/capture), so the highlight never flickers.
  useLayoutEffect(() => {
    const root = treeRef.current;
    if (root === null) return;
    root.querySelector(`.${styles.selected}`)?.classList.remove(styles.selected);
    if (selected !== null) {
      root.querySelector(`[data-path="${selected.path}"]`)?.classList.add(styles.selected);
    }
  }, [selected, tree]);

  const selectedEntries = useMemo(
    () => (selected === null ? [] : styleEntries(selected.node)),
    [selected],
  );

  const state = connected
    ? { cls: styles.live, label: "Live" }
    : enabled
      ? { cls: styles.waiting, label: "Waiting for game" }
      : { cls: "", label: "Bridge off" };

  return (
    <div className={styles.root}>
      <div className={styles.toolbar}>
        <Button size="sm" onClick={() => void capture()} disabled={!connected || capturing}>
          <RefreshCw size={12} /> Capture
        </Button>
        {dump !== null && (
          <label className={styles.searchWrap}>
            <Search size={12} className={styles.searchIcon} />
            <input
              className={styles.search}
              type="text"
              placeholder="Filter by type, #name, .class…"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              spellCheck={false}
            />
          </label>
        )}
        <span className={`${styles.status} ${state.cls}`}>
          <RadioTower size={12} /> {state.label}
        </span>
      </div>

      {renderBody()}

      {dump !== null && selected !== null && (
        <div className={styles.stylePanel}>
          <div className={styles.stylePanelHead}>
            {selected.node.type ?? "?"}
            {selected.node.name ? ` #${selected.node.name}` : ""}
          </div>
          {selectedEntries.length === 0 ? (
            <div className={styles.stylePanelEmpty}>No computed styles for this node.</div>
          ) : (
            <div className={styles.styleList}>
              {selectedEntries.map((e) => (
                <div key={e.key} className={styles.styleRow}>
                  <span className={styles.styleKey}>{e.key}</span>
                  <span className={styles.styleVal}>
                    {e.color !== null && (
                      <span className={styles.swatch} style={{ background: e.color }} />
                    )}
                    {e.value}
                  </span>
                </div>
              ))}
            </div>
          )}
        </div>
      )}

      {menu !== null && (
        <ContextMenu x={menu.x} y={menu.y} items={menu.items} onClose={() => setMenu(null)} />
      )}
    </div>
  );

  function renderBody() {
    if (capturing) return <LoadingBanner label="Capturing UI tree…" />;
    if (error !== null) {
      return (
        <EmptyState
          title="Capture failed"
          reason={error}
          action={
            <Button size="sm" onClick={() => void capture()} disabled={!connected}>
              Retry
            </Button>
          }
        />
      );
    }
    if (!enabled) {
      return (
        <EmptyState
          title="Live bridge is off"
          reason="Enable the live game bridge in Settings, then capture the running game's UI."
        />
      );
    }
    if (!connected) {
      return (
        <EmptyState
          title="Waiting for the game"
          reason="The bridge is enabled but not connected. Launch the game and it will connect, then capture."
        />
      );
    }
    if (dump === null) {
      return (
        <EmptyState
          title="No capture yet"
          reason="Navigate to a screen in the game, then capture its live UI tree."
          action={
            <Button size="sm" onClick={() => void capture()}>
              <RefreshCw size={12} /> Capture
            </Button>
          }
        />
      );
    }
    return tree;
  }
}
