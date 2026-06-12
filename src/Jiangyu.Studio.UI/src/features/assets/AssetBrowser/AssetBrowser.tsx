import uFuzzy from "@leeoniya/ufuzzy";
import { useVirtualizer } from "@tanstack/react-virtual";
import { useCallback, useDeferredValue, useEffect, useMemo, useRef, useState } from "react";
import {
  assetsExport,
  assetsIndex,
  assetsIndexStatus,
  assetsPreview,
  assetsSearch,
  buildNameUniquenessMap,
  classifyAsset,
  isPrefab,
  passesKindFilter,
  pickDirectory,
  revealInExplorer,
  ASSET_KIND_GROUP_CLASSES,
  ASSET_KIND_GROUP_LABEL,
  type AssetEntry,
  type AssetIndexStatus,
  type AssetKindGroup,
} from "@features/assets/assets";
import { unityImportPrefab } from "@features/unity/unity";
import {
  getProjectConfig,
  setProjectAssetExportPath,
  type ProjectConfig,
} from "@features/project/config";
import type { AssetPreviewResult } from "@shared/rpc";
import { isAbsolute, join, normalise } from "@shared/path";
import { useToastPush } from "@shared/toast";
import { DEFAULT_ASSET_BROWSER_STATE, type AssetBrowserState } from "@features/panes/browserState";
import { useDebouncedScrollTop } from "@shared/utils/useDebouncedScrollTop";
import { useDismissOnOutsideClick } from "@shared/utils/useDismissOnOutsideClick";
import { Spinner } from "@shared/ui/Spinner/Spinner";
import { LoadingBanner } from "@shared/ui/LoadingBanner/LoadingBanner";
import { EmptyState } from "@shared/ui/EmptyState/EmptyState";
import { Button } from "@shared/ui/Button/Button";
import { AssetDetails, type Destination } from "./AssetDetails";
import type { BatchProgress } from "./BatchProgressBar";
import styles from "./AssetBrowser.module.css";

interface AssetBrowserProps {
  projectPath: string;
  initialState?: AssetBrowserState | undefined;
  onStateChange?: ((state: AssetBrowserState) => void) | undefined;
}

type KindFilter = "all" | AssetKindGroup;

const rowKey = (a: AssetEntry): string => `${a.collection ?? ""}:${a.pathId}`;

const KIND_FILTERS: readonly ("all" | AssetKindGroup)[] = [
  "all",
  ...(Object.keys(ASSET_KIND_GROUP_CLASSES) as AssetKindGroup[]),
];

export function AssetBrowser({ projectPath, initialState, onStateChange }: AssetBrowserProps) {
  const [status, setStatus] = useState<AssetIndexStatus | null>(null);
  const [indexing, setIndexing] = useState(false);
  const [indexError, setIndexError] = useState<string | null>(null);

  // Filter / query / selection / listFraction are controlled through
  // initialState so the parent can persist them (per pane in layout, per
  // pane window in a descriptor). initialState is read once on mount; further
  // updates are pushed out through onStateChange.
  const [query, setQuery] = useState(
    () => initialState?.query ?? DEFAULT_ASSET_BROWSER_STATE.query,
  );
  const [kindFilter, setKindFilter] = useState<KindFilter>(
    () => initialState?.kindFilter ?? DEFAULT_ASSET_BROWSER_STATE.kindFilter,
  );
  const [allAssets, setAllAssets] = useState<readonly AssetEntry[]>([]);
  const [loadingAssets, setLoadingAssets] = useState(false);

  const [selection, setSelection] = useState<ReadonlySet<string>>(
    () => new Set(initialState?.selection ?? []),
  );
  const [focusedKey, setFocusedKey] = useState<string | null>(
    () => initialState?.focusedKey ?? DEFAULT_ASSET_BROWSER_STATE.focusedKey,
  );
  const lastClickedRef = useRef<string | null>(null);

  const [projectConfig, setProjectConfig] = useState<ProjectConfig>({});

  const [exporting, setExporting] = useState(false);
  const [exportProgress, setExportProgress] = useState<BatchProgress | null>(null);
  const [importing, setImporting] = useState(false);
  const [importProgress, setImportProgress] = useState<BatchProgress | null>(null);
  const [actionMenuOpen, setActionMenuOpen] = useState(false);
  const actionMenuRef = useRef<HTMLDivElement>(null);

  // Resizable split between list and details panels
  const [listFraction, setListFraction] = useState(
    () => initialState?.listFraction ?? DEFAULT_ASSET_BROWSER_STATE.listFraction,
  );
  const [landscape, setLandscape] = useState(false);
  const bodyRef = useRef<HTMLDivElement>(null);

  const [scrollTop, handleListScroll] = useDebouncedScrollTop(
    initialState?.scrollTop ?? DEFAULT_ASSET_BROWSER_STATE.scrollTop,
  );

  // Emit the persisted state slice back to the parent so it can round-trip
  // via layout / pane-window descriptor. Fires on every relevant change;
  // parent is expected to dedupe if needed.
  const onStateChangeRef = useRef(onStateChange);
  useEffect(() => {
    onStateChangeRef.current = onStateChange;
  }, [onStateChange]);
  useEffect(() => {
    onStateChangeRef.current?.({
      query,
      kindFilter,
      selection: Array.from(selection),
      focusedKey,
      listFraction,
      scrollTop,
    });
  }, [query, kindFilter, selection, focusedKey, listFraction, scrollTop]);

  useEffect(() => {
    const el = bodyRef.current;
    if (!el) return;
    const ro = new ResizeObserver((entries) => {
      for (const entry of entries) {
        const { width, height } = entry.contentRect;
        setLandscape(width > height * (5 / 3));
      }
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  // Asset preview state — guarded by a token so stale responses from a
  // previously focused asset don't overwrite the current preview. Stored as
  // raw base64 (not a data: URL) because ModelViewer can't fetch() large
  // data: URLs under WebKitGTK.
  const [previewData, setPreviewData] = useState<AssetPreviewResult | null>(null);
  const [previewLoading, setPreviewLoading] = useState(false);
  const previewTokenRef = useRef(0);

  const pushToast = useToastPush();

  useEffect(() => {
    let cancelled = false;
    void assetsIndexStatus()
      .then((s) => {
        if (!cancelled) setStatus(s);
      })
      .catch((err: unknown) => {
        if (!cancelled) {
          setStatus({
            state: "noGame",
            reason: err instanceof Error ? err.message : String(err),
          });
        }
      });
    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    let cancelled = false;
    void getProjectConfig(projectPath)
      .then((c) => {
        if (!cancelled) setProjectConfig(c);
      })
      .catch((err: unknown) => {
        console.error("[AssetBrowser] getProjectConfig failed:", err);
      });
    return () => {
      cancelled = true;
    };
  }, [projectPath]);

  // Fetch a lazy on-demand preview when the focused asset changes.
  // Uses a token to discard stale responses from a previously focused asset.
  const PREVIEWABLE_CLASSES = useMemo(
    () =>
      new Set(["Texture2D", "Sprite", "AudioClip", "GameObject", "PrefabHierarchyObject", "Mesh"]),
    [],
  );
  // Keyed against the full catalogue rather than `results`: ctrl-click picks
  // must survive narrowing the filter (user intent is "keep this one", not
  // "keep only if still visible"), and the focused asset must resolve even
  // when the kind filter or search hides its row so the details panel and
  // preview agree.
  const assetsByKey = useMemo(() => {
    const m = new Map<string, AssetEntry>();
    for (const a of allAssets) m.set(rowKey(a), a);
    return m;
  }, [allAssets]);

  // Single source of truth for the focused asset, shared by the preview reset
  // block, the preview fetch effect, and the details panel.
  const focused = useMemo(
    () => (focusedKey === null ? null : (assetsByKey.get(focusedKey) ?? null)),
    [assetsByKey, focusedKey],
  );

  // Reset preview state synchronously off focusedKey identity so the panel
  // clears the moment focus moves rather than one render after the effect.
  const [prevFocusedKey, setPrevFocusedKey] = useState(focusedKey);
  if (prevFocusedKey !== focusedKey) {
    setPrevFocusedKey(focusedKey);
    setPreviewData(null);
    const previewable =
      focused !== null &&
      Boolean(focused.collection) &&
      PREVIEWABLE_CLASSES.has(focused.className ?? "");
    setPreviewLoading(previewable);
  }

  useEffect(() => {
    if (focused === null) return;
    const className = focused.className ?? "";
    if (!focused.collection || !PREVIEWABLE_CLASSES.has(className)) return;

    const token = ++previewTokenRef.current;
    void assetsPreview({ collection: focused.collection, pathId: focused.pathId, className })
      .then((result) => {
        if (previewTokenRef.current !== token) return;
        if (result) setPreviewData(result);
      })
      .catch((err: unknown) => {
        console.warn("[AssetBrowser] preview failed:", err);
      })
      .finally(() => {
        if (previewTokenRef.current === token) setPreviewLoading(false);
      });
  }, [focused, PREVIEWABLE_CLASSES]);

  // Reset catalogue state synchronously off the status state so the list
  // clears and the spinner shows the moment status changes.
  const [prevStatusState, setPrevStatusState] = useState(status?.state);
  if (prevStatusState !== status?.state) {
    setPrevStatusState(status?.state);
    if (status?.state === "current") {
      setLoadingAssets(true);
    } else {
      setAllAssets([]);
    }
  }

  // Load the full index once the catalogue is current. All filtering + fuzzy
  // search happens client-side via uFuzzy against this list — the host's
  // Search only does substring matching, which misses the typical "type a
  // few letters" workflow.
  useEffect(() => {
    if (status?.state !== "current") return;
    let cancelled = false;
    void assetsSearch({ limit: 200_000 })
      .then((rows) => {
        if (cancelled) return;
        // Sort by name so the default (no-filter, no-query) view interleaves
        // kinds and collections — otherwise the first 200 rows are whatever
        // the host's collection-iteration order yielded first (e.g. all of
        // resources.assets, with prefabs/audio/etc. far past the cap).
        const sorted = [...rows].sort((a, b) => (a.name ?? "").localeCompare(b.name ?? ""));
        setAllAssets(sorted);
      })
      .catch((err: unknown) => {
        console.error("[AssetBrowser] assetsSearch failed:", err);
        if (!cancelled) setAllAssets([]);
      })
      .finally(() => {
        if (!cancelled) setLoadingAssets(false);
      });
    return () => {
      cancelled = true;
    };
  }, [status?.state]);

  const nameUniqueness = useMemo(() => buildNameUniquenessMap(allAssets), [allAssets]);

  // uFuzzy is designed for 100k+ haystacks — typical full-catalogue search
  // comes in under 20ms. The haystack is the asset-name list, stable across
  // filter changes; the kind filter applies to the ranked results so we don't
  // rebuild the string array when the user toggles filter pills.
  const haystack = useMemo(() => allAssets.map((a) => a.name ?? ""), [allAssets]);
  const uf = useMemo(() => new uFuzzy({ intraMode: 1 }), []);

  const deferredQuery = useDeferredValue(query);

  // Denominator for the list footer, sharing the same filter the `results`
  // memo uses so the ratio matches the visible pool.
  const kindFilteredCount = useMemo(
    () => allAssets.reduce((n, a) => (passesKindFilter(a, kindFilter) ? n + 1 : n), 0),
    [allAssets, kindFilter],
  );

  const results = useMemo<readonly AssetEntry[]>(() => {
    const q = deferredQuery.trim();
    if (q.length === 0) {
      return allAssets.filter((a) => passesKindFilter(a, kindFilter));
    }

    const [idxs, info, order] = uf.search(haystack, q);
    if (idxs === null) return [];

    const out: AssetEntry[] = [];
    if (info !== null) {
      for (const oi of order) {
        const assetIdx = info.idx[oi];
        if (assetIdx === undefined) continue;
        const a = allAssets[assetIdx];
        if (a === undefined || !passesKindFilter(a, kindFilter)) continue;
        out.push(a);
      }
    } else {
      for (const assetIdx of idxs) {
        const a = allAssets[assetIdx];
        if (a === undefined || !passesKindFilter(a, kindFilter)) continue;
        out.push(a);
      }
    }
    return out;
  }, [allAssets, haystack, uf, deferredQuery, kindFilter]);

  // Prune selection / focus when the underlying catalogue reloads. Intentionally
  // NOT gated on `results`: narrowing the filter or search should preserve the
  // user's multi-select so they can widen back without re-picking.
  //
  // Skip while the catalogue hasn't loaded yet — on initial mount allAssets is
  // empty and would wipe a restored selection / focus before the data arrives.
  // Computed off allAssets identity rather than via useEffect so pruning happens
  // in the same render that surfaces the new catalogue.
  const [prevAllAssets, setPrevAllAssets] = useState(allAssets);
  if (prevAllAssets !== allAssets && allAssets.length > 0) {
    setPrevAllAssets(allAssets);
    const keys = new Set(allAssets.map(rowKey));
    setSelection((prev) => {
      if (prev.size === 0) return prev;
      let changed = false;
      const next = new Set<string>();
      for (const k of prev) {
        if (keys.has(k)) next.add(k);
        else changed = true;
      }
      return changed ? next : prev;
    });
    setFocusedKey((prev) => (prev !== null && keys.has(prev) ? prev : null));
  }

  const listScrollRef = useRef<HTMLDivElement>(null);
  // eslint-disable-next-line react-hooks/incompatible-library -- TanStack Virtual returns non-memoisable functions; the only API the library exposes.
  const virtualiser = useVirtualizer({
    count: results.length,
    getScrollElement: () => listScrollRef.current,
    estimateSize: () => 28,
    overscan: 20,
  });

  // Restore persisted scroll position once the list has actual rows to scroll
  // over. Gated on results so we don't seed before the virtualiser has sized
  // anything — otherwise scrollTop would be clamped to 0.
  const scrollRestoredRef = useRef(false);
  useEffect(() => {
    if (scrollRestoredRef.current) return;
    if (results.length === 0) return;
    const target = initialState?.scrollTop ?? 0;
    if (target > 0 && listScrollRef.current !== null) {
      listScrollRef.current.scrollTop = target;
    }
    scrollRestoredRef.current = true;
  }, [results, initialState]);

  const handleSplitDrag = useCallback(
    (e: React.MouseEvent) => {
      e.preventDefault();
      const body = bodyRef.current;
      if (!body) return;
      const startPos = landscape ? e.clientY : e.clientX;
      const startFraction = listFraction;
      const extent = landscape ? body.clientHeight : body.clientWidth;
      const prevCursor = document.body.style.cursor;
      const prevSelect = document.body.style.userSelect;
      document.body.style.cursor = landscape ? "row-resize" : "col-resize";
      document.body.style.userSelect = "none";

      let pending: number | null = null;
      let raf: number | null = null;
      const flush = () => {
        raf = null;
        if (pending === null) return;
        const delta = pending;
        pending = null;
        const next = startFraction + delta / extent;
        setListFraction(Math.max(0.2, Math.min(0.8, next)));
      };
      const onMove = (ev: MouseEvent) => {
        pending = (landscape ? ev.clientY : ev.clientX) - startPos;
        raf ??= requestAnimationFrame(flush);
      };
      const onUp = () => {
        document.removeEventListener("mousemove", onMove);
        document.removeEventListener("mouseup", onUp);
        if (raf !== null) cancelAnimationFrame(raf);
        flush();
        document.body.style.cursor = prevCursor;
        document.body.style.userSelect = prevSelect;
      };
      document.addEventListener("mousemove", onMove);
      document.addEventListener("mouseup", onUp);
    },
    [listFraction, landscape],
  );

  const handleIndex = useCallback(async () => {
    setIndexing(true);
    setIndexError(null);
    try {
      const s = await assetsIndex();
      setStatus(s);
    } catch (err) {
      setIndexError((err as Error).message);
    } finally {
      setIndexing(false);
    }
  }, []);

  const selectedRows = useMemo<readonly AssetEntry[]>(() => {
    const out: AssetEntry[] = [];
    for (const k of selection) {
      const a = assetsByKey.get(k);
      if (a) out.push(a);
    }
    return out;
  }, [selection, assetsByKey]);

  const toggleSelection = useCallback((key: string) => {
    setSelection((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
    lastClickedRef.current = key;
  }, []);

  const handleRowClick = useCallback(
    (row: AssetEntry, event: React.MouseEvent) => {
      const key = rowKey(row);
      setFocusedKey(key);

      if (event.shiftKey && lastClickedRef.current !== null) {
        const keys = results.map(rowKey);
        const a = keys.indexOf(lastClickedRef.current);
        const b = keys.indexOf(key);
        if (a !== -1 && b !== -1) {
          const [lo, hi] = a < b ? [a, b] : [b, a];
          const range = keys.slice(lo, hi + 1);
          setSelection((prev) => {
            const next = new Set(prev);
            for (const k of range) next.add(k);
            return next;
          });
          return;
        }
      }

      if (event.ctrlKey || event.metaKey) {
        toggleSelection(key);
        return;
      }

      setSelection(new Set([key]));
      lastClickedRef.current = key;
    },
    [results, toggleSelection],
  );

  // "Select all" refers to the currently-visible rows: if every visible row is
  // already selected, clear those (leaving hidden selections intact); otherwise
  // union the visible rows into the selection.
  const handleSelectAll = useCallback(() => {
    const visibleKeys = results.map(rowKey);
    const allVisibleSelected = visibleKeys.length > 0 && visibleKeys.every((k) => selection.has(k));
    setSelection((prev) => {
      const next = new Set(prev);
      if (allVisibleSelected) {
        for (const k of visibleKeys) next.delete(k);
      } else {
        for (const k of visibleKeys) next.add(k);
      }
      return next;
    });
  }, [results, selection]);

  const visibleAllSelected = useMemo(() => {
    if (results.length === 0) return false;
    for (const r of results) if (!selection.has(rowKey(r))) return false;
    return true;
  }, [results, selection]);

  const visibleAnySelected = useMemo(() => {
    for (const r of results) if (selection.has(rowKey(r))) return true;
    return false;
  }, [results, selection]);

  const projectExportPath = projectConfig.assetExportPath
    ? isAbsolute(projectConfig.assetExportPath)
      ? projectConfig.assetExportPath
      : join(projectPath, projectConfig.assetExportPath)
    : null;

  const defaultBaseDir = useMemo(() => join(projectPath, "exported"), [projectPath]);

  const resolveBaseDir = useCallback(
    async (dest: Destination): Promise<string | null> => {
      if (dest === "default") return defaultBaseDir;
      if (dest === "project") return projectExportPath;
      return await pickDirectory({ title: "Export assets to…" });
    },
    [defaultBaseDir, projectExportPath],
  );

  // Shared skeleton for the bulk export/import flows: flag the busy state,
  // resolve the per-row operation, run it sequentially with {done, total}
  // progress, then toast success (with a Reveal action on the first produced
  // path) or failure, resetting busy/progress either way.
  const runBatch = useCallback(
    (opts: {
      rows: readonly AssetEntry[];
      setBusy: (busy: boolean) => void;
      setProgress: (progress: BatchProgress | null) => void;
      /** Runs after the paint guard; returning null aborts without a toast. */
      makeOp: () => Promise<((row: AssetEntry) => Promise<string>) | null>;
      successMessage: (count: number) => string;
      errorLabel: string;
    }) => {
      const { rows, setBusy, setProgress, makeOp, successMessage, errorLabel } = opts;
      setBusy(true);

      // Use double-rAF to guarantee the browser has painted the disabled/spinner
      // state before kicking off the RPC loop.
      requestAnimationFrame(() => {
        requestAnimationFrame(() => {
          const run = async () => {
            try {
              const op = await makeOp();
              if (op === null) return;

              setProgress({ done: 0, total: rows.length });
              const paths: string[] = [];
              for (const [i, row] of rows.entries()) {
                paths.push(await op(row));
                setProgress({ done: i + 1, total: rows.length });
              }
              const first = paths[0];
              pushToast({
                variant: "success",
                message: successMessage(paths.length),
                ...(first != null ? { detail: first } : {}),
                ...(first != null
                  ? { actions: [{ label: "Reveal", run: () => void revealInExplorer(first) }] }
                  : {}),
              });
            } catch (err) {
              pushToast({
                variant: "error",
                message: `${errorLabel} failed: ${(err as Error).message}`,
              });
            } finally {
              setBusy(false);
              setProgress(null);
            }
          };
          void run();
        });
      });
    },
    [pushToast],
  );

  const handleExport = useCallback(
    (dest: Destination) => {
      if (selectedRows.length === 0 || exporting) return;
      runBatch({
        rows: selectedRows,
        setBusy: setExporting,
        setProgress: setExportProgress,
        makeOp: async () => {
          const baseDir = await resolveBaseDir(dest);
          if (baseDir === null) return null;
          return async (row) =>
            (
              await assetsExport({
                assetName: row.name ?? "unnamed",
                collection: row.collection ?? "",
                pathId: row.pathId,
                kind: row.className ?? "",
                baseDir,
              })
            ).outputPath;
        },
        successMessage: (count) => `Exported ${count} asset${count === 1 ? "" : "s"}`,
        errorLabel: "Export",
      });
    },
    [selectedRows, exporting, resolveBaseDir, runBatch],
  );

  useDismissOnOutsideClick(actionMenuRef, () => setActionMenuOpen(false), {
    enabled: actionMenuOpen,
  });

  const allSelectedArePrefabs = useMemo(
    () => selectedRows.length > 0 && selectedRows.every(isPrefab),
    [selectedRows],
  );

  const handleImport = useCallback(() => {
    if (selectedRows.length === 0 || importing) return;
    if (!selectedRows.every(isPrefab)) return;
    runBatch({
      rows: selectedRows,
      setBusy: setImporting,
      setProgress: setImportProgress,
      makeOp: () =>
        Promise.resolve(async (row: AssetEntry) => {
          const params: {
            assetName: string;
            pathId?: number;
            collection?: string;
          } = { assetName: row.name ?? "unnamed", pathId: row.pathId };
          if (row.collection) params.collection = row.collection;
          return (await unityImportPrefab(params)).destDir;
        }),
      successMessage: (count) => `Imported ${count} prefab${count === 1 ? "" : "s"} to Unity`,
      errorLabel: "Import",
    });
  }, [selectedRows, importing, runBatch]);

  const handleConfigureProjectPath = useCallback(async () => {
    const picked = await pickDirectory({
      title: "Pick project export folder",
      initial: projectPath,
    });
    if (picked === null) return;
    // Normalise separators so the comparison works on Windows (native picker
    // returns backslashes) and store as a project-relative path when inside the
    // project root so moving the project folder doesn't break the reference.
    const norm = normalise(picked);
    const normRoot = normalise(projectPath);
    const rel =
      norm.startsWith(normRoot + "/") || norm === normRoot
        ? norm.substring(normRoot.length).replace(/^\//, "") || "."
        : norm;
    const updated = await setProjectAssetExportPath(projectPath, rel);
    setProjectConfig(updated);
  }, [projectPath]);

  if (status === null) {
    return (
      <div className={styles.root}>
        <LoadingBanner label="Checking asset index…" />
      </div>
    );
  }

  if (status.state !== "current") {
    const missing = status.state === "missing";
    const stale = status.state === "stale";
    const noGame = status.state === "noGame";
    const title = noGame
      ? "Game path not configured"
      : missing
        ? "No asset index yet"
        : "Asset index is out of date";
    const reasons = [status.reason, indexError].filter((r): r is string => Boolean(r));
    return (
      <div className={styles.root}>
        <EmptyState
          title={title}
          reason={reasons}
          action={
            !noGame && (
              <Button variant="primary" disabled={indexing} onClick={() => void handleIndex()}>
                {indexing && (
                  <Spinner
                    size={12}
                    trackColor="var(--track-on-accent)"
                    accentColor="var(--paper-0)"
                  />
                )}
                {indexing ? "Indexing…" : stale ? "Re-index" : "Index assets"}
              </Button>
            )
          }
        />
      </div>
    );
  }

  return (
    <div className={styles.root}>
      <div className={styles.toolbar}>
        <input
          type="search"
          className={styles.search}
          placeholder="Search by name…"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
        />
        <div className={styles.kindPills}>
          {KIND_FILTERS.map((k) => (
            <button
              key={k}
              type="button"
              className={`${styles.kindPill} ${kindFilter === k ? styles.kindPillActive : ""}`}
              onClick={() => setKindFilter(k)}
            >
              {k === "all" ? "All" : ASSET_KIND_GROUP_LABEL[k]}
            </button>
          ))}
        </div>
      </div>

      <div className={`${styles.body}${landscape ? ` ${styles.landscape}` : ""}`} ref={bodyRef}>
        <div className={styles.listPanel} style={{ flex: `0 0 ${listFraction * 100}%` }}>
          <div className={styles.listHeader}>
            <span className={styles.listHeaderCheck}>
              <input
                type="checkbox"
                checked={visibleAllSelected}
                ref={(el) => {
                  if (el) el.indeterminate = !visibleAllSelected && visibleAnySelected;
                }}
                onChange={handleSelectAll}
              />
            </span>
            <span>Name</span>
            <span>Type</span>
          </div>
          <div className={styles.list} ref={listScrollRef} onScroll={handleListScroll}>
            {loadingAssets ? (
              <div className={styles.detailsEmpty}>Loading index…</div>
            ) : results.length === 0 ? (
              <div className={styles.detailsEmpty}>
                {query || kindFilter !== "all"
                  ? "No matches"
                  : `${status.assetCount ?? 0} assets indexed. Start typing to search.`}
              </div>
            ) : (
              <div style={{ height: virtualiser.getTotalSize(), position: "relative" }}>
                {virtualiser.getVirtualItems().map((vRow) => {
                  const row = results[vRow.index];
                  if (row === undefined) return null;
                  const key = rowKey(row);
                  const selected = selection.has(key);
                  const focused = focusedKey === key;
                  const group = classifyAsset(row.className);
                  return (
                    <button
                      key={key}
                      type="button"
                      ref={virtualiser.measureElement}
                      data-index={vRow.index}
                      className={`${styles.row} ${selected ? styles.rowSelected : ""} ${focused ? styles.rowFocused : ""}`}
                      title={row.name ?? "(unnamed)"}
                      style={{
                        position: "absolute",
                        top: 0,
                        left: 0,
                        width: "100%",
                        transform: `translateY(${vRow.start}px)`,
                      }}
                      onClick={(e) => handleRowClick(row, e)}
                    >
                      <span
                        className={styles.rowCheck}
                        role="presentation"
                        onClick={(e) => {
                          e.stopPropagation();
                          toggleSelection(key);
                        }}
                      >
                        <input
                          type="checkbox"
                          checked={selected}
                          onChange={() => {
                            toggleSelection(key);
                          }}
                          aria-label={`Select ${row.name ?? "asset"}`}
                        />
                      </span>
                      <span className={styles.rowName}>{row.name ?? "(unnamed)"}</span>
                      <span className={styles.rowKind}>
                        {group ? ASSET_KIND_GROUP_LABEL[group] : (row.className ?? "—")}
                      </span>
                    </button>
                  );
                })}
              </div>
            )}
          </div>
          <div className={styles.listFooter}>
            {results.length.toLocaleString()} / {kindFilteredCount.toLocaleString()} assets
          </div>
        </div>

        {/* Resize handle — pointer-driven separator; the rule doesn't
            recognise the focusable variant of separator. */}
        {/* eslint-disable-next-line jsx-a11y/no-noninteractive-element-interactions */}
        <div
          className={styles.splitHandle}
          role="separator"
          aria-orientation="vertical"
          aria-label="Resize details panel"
          tabIndex={-1}
          onMouseDown={handleSplitDrag}
        />

        <div className={styles.detailsPanel}>
          <AssetDetails
            focused={focused}
            previewData={previewData}
            previewLoading={previewLoading}
            nameUniqueness={nameUniqueness}
            actionMenuRef={actionMenuRef}
            actionMenuOpen={actionMenuOpen}
            onToggleActionMenu={() => setActionMenuOpen((v) => !v)}
            onCloseActionMenu={() => setActionMenuOpen(false)}
            onExport={handleExport}
            onImport={handleImport}
            onConfigureProjectPath={() => void handleConfigureProjectPath()}
            projectExportPath={projectExportPath}
            selectedCount={selectedRows.length}
            exporting={exporting}
            exportProgress={exportProgress}
            importing={importing}
            importProgress={importProgress}
            allSelectedArePrefabs={allSelectedArePrefabs}
          />
        </div>
      </div>
    </div>
  );
}
