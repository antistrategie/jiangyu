import uFuzzy from "@leeoniya/ufuzzy";
import { useCallback, useDeferredValue, useEffect, useMemo, useRef, useState } from "react";
import {
  assetsExport,
  assetsIndex,
  assetsIndexStatus,
  assetsPreview,
  assetsSearch,
  classifyAsset,
  pickDirectory,
  revealInExplorer,
  ASSET_KIND_GROUP_CLASSES,
  ASSET_KIND_GROUP_LABEL,
  EXPORTABLE_CLASS_NAMES,
  type AssetEntry,
  type AssetIndexStatus,
  type AssetKindGroup,
} from "../../lib/assets.ts";
import {
  getProjectConfig,
  setProjectAssetExportPath,
  type ProjectConfig,
} from "../../lib/projectConfig.ts";
import { isAbsolute, join, normalise } from "../../lib/path.ts";
import { useToast } from "../../lib/toast.tsx";
import { ModelViewer } from "./ModelViewer.tsx";
import styles from "./AssetBrowser.module.css";

interface AssetBrowserProps {
  projectPath: string;
}

type KindFilter = "all" | AssetKindGroup;
type Destination = "default" | "project" | "custom";

const rowKey = (a: AssetEntry): string => `${a.collection ?? ""}:${a.pathId}`;

// Visible row cap. Reconciling hundreds of grid rows on every keystroke is
// visible lag even with useDeferredValue; 200 is enough for "find the match,
// refine if needed" without making the filter feel sluggish.
const RESULT_CAP = 200;

const KIND_FILTERS: readonly ("all" | AssetKindGroup)[] = [
  "all",
  ...(Object.keys(ASSET_KIND_GROUP_CLASSES) as AssetKindGroup[]),
];

export function AssetBrowser({ projectPath }: AssetBrowserProps) {
  const [status, setStatus] = useState<AssetIndexStatus | null>(null);
  const [indexing, setIndexing] = useState(false);
  const [indexError, setIndexError] = useState<string | null>(null);

  const [query, setQuery] = useState("");
  const [kindFilter, setKindFilter] = useState<KindFilter>("all");
  const [allAssets, setAllAssets] = useState<readonly AssetEntry[]>([]);
  const [loadingAssets, setLoadingAssets] = useState(false);

  const [selection, setSelection] = useState<ReadonlySet<string>>(new Set());
  const [focusedKey, setFocusedKey] = useState<string | null>(null);
  const lastClickedRef = useRef<string | null>(null);

  const [projectConfig, setProjectConfig] = useState<ProjectConfig>({});
  const [destination, setDestination] = useState<Destination>("default");

  const [exporting, setExporting] = useState(false);
  const [exportProgress, setExportProgress] = useState<{ done: number; total: number } | null>(
    null,
  );

  // Asset preview state — guarded by a token so stale responses from a
  // previously focused asset don't overwrite the current preview.
  const [previewData, setPreviewData] = useState<{
    dataUrl: string;
    mimeType: string;
  } | null>(null);
  const [previewLoading, setPreviewLoading] = useState(false);
  const previewTokenRef = useRef(0);

  const { push: pushToast } = useToast();

  useEffect(() => {
    let cancelled = false;
    void assetsIndexStatus()
      .then((s) => {
        if (!cancelled) setStatus(s);
      })
      .catch((err: Error) => {
        if (!cancelled) setStatus({ state: "noGame", reason: err.message });
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
      .catch((err: Error) => {
        console.error("[AssetBrowser] getProjectConfig failed:", err);
      });
    return () => {
      cancelled = true;
    };
  }, [projectPath]);

  // Fetch a lazy on-demand preview when the focused asset changes.
  // Uses a token to discard stale responses from a previously focused asset.
  const PREVIEWABLE_CLASSES = useMemo(
    () => new Set(["Texture2D", "Sprite", "AudioClip", "GameObject", "PrefabHierarchyObject", "Mesh"]),
    [],
  );
  useEffect(() => {
    const token = ++previewTokenRef.current;
    setPreviewData(null);

    if (focusedKey === null) {
      setPreviewLoading(false);
      return;
    }
    const focused = allAssets.find((a) => rowKey(a) === focusedKey);
    const className = focused?.className ?? "";
    if (!focused || !focused.collection || !PREVIEWABLE_CLASSES.has(className)) {
      setPreviewLoading(false);
      return;
    }

    setPreviewLoading(true);
    void assetsPreview({ collection: focused.collection, pathId: focused.pathId, className })
      .then((result) => {
        if (previewTokenRef.current !== token) return;
        if (result) {
          setPreviewData({
            dataUrl: `data:${result.mimeType};base64,${result.data}`,
            mimeType: result.mimeType,
          });
        }
      })
      .catch((err: unknown) => {
        console.warn("[AssetBrowser] preview failed:", err);
      })
      .finally(() => {
        if (previewTokenRef.current === token) setPreviewLoading(false);
      });
  }, [focusedKey, allAssets, PREVIEWABLE_CLASSES]);

  // Load the full index once the catalogue is current. All filtering + fuzzy
  // search happens client-side via uFuzzy against this list — the host's
  // Search only does substring matching, which misses the typical "type a
  // few letters" workflow.
  useEffect(() => {
    if (status?.state !== "current") {
      setAllAssets([]);
      return;
    }
    let cancelled = false;
    setLoadingAssets(true);
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
      .catch((err: Error) => {
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

  // uFuzzy is designed for 100k+ haystacks — typical full-catalogue search
  // comes in under 20ms. The haystack is the asset-name list, stable across
  // filter changes; the kind filter applies to the ranked results so we don't
  // rebuild the string array when the user toggles filter pills.
  const haystack = useMemo(() => allAssets.map((a) => a.name ?? ""), [allAssets]);
  const uf = useMemo(() => new uFuzzy({ intraMode: 1 }), []);

  const deferredQuery = useDeferredValue(query);
  const results = useMemo<readonly AssetEntry[]>(() => {
    const kindClasses = kindFilter === "all" ? null : ASSET_KIND_GROUP_CLASSES[kindFilter];
    const passes = (a: AssetEntry): boolean => {
      if (a.className === null) return false;
      if (kindClasses === null) return EXPORTABLE_CLASS_NAMES.has(a.className);
      return kindClasses.includes(a.className);
    };

    const q = deferredQuery.trim();
    if (q.length === 0) {
      const out: AssetEntry[] = [];
      for (const a of allAssets) {
        if (!passes(a)) continue;
        out.push(a);
        if (out.length >= RESULT_CAP) break;
      }
      return out;
    }

    const [idxs, info, order] = uf.search(haystack, q);
    if (idxs === null) return [];

    const out: AssetEntry[] = [];
    if (info !== null && order !== null) {
      for (const oi of order) {
        const assetIdx = info.idx[oi]!;
        const a = allAssets[assetIdx]!;
        if (!passes(a)) continue;
        out.push(a);
        if (out.length >= RESULT_CAP) break;
      }
    } else {
      for (const assetIdx of idxs) {
        const a = allAssets[assetIdx]!;
        if (!passes(a)) continue;
        out.push(a);
        if (out.length >= RESULT_CAP) break;
      }
    }
    return out;
  }, [allAssets, haystack, uf, kindFilter, deferredQuery]);

  // Prune selection / focus when the underlying catalogue reloads. Intentionally
  // NOT gated on `results`: narrowing the filter or search should preserve the
  // user's multi-select so they can widen back without re-picking.
  useEffect(() => {
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
  }, [allAssets]);

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

  // Looking up selected assets via `results` would silently drop picks whenever
  // the user narrows the filter or the RESULT_CAP truncates them — the user
  // intent on ctrl-click is "keep this one", not "keep only if still visible".
  // Key the selection against `allAssets` so hidden picks still export.
  const assetsByKey = useMemo(() => {
    const m = new Map<string, AssetEntry>();
    for (const a of allAssets) m.set(rowKey(a), a);
    return m;
  }, [allAssets]);

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

  const defaultBaseDir = useMemo(() => join(projectPath, ".jiangyu", "exports"), [projectPath]);

  const resolveBaseDir = useCallback(async (): Promise<string | null> => {
    if (destination === "default") return defaultBaseDir;
    if (destination === "project") return projectExportPath;
    return await pickDirectory({ title: "Export assets to…" });
  }, [destination, defaultBaseDir, projectExportPath]);

  const handleExport = useCallback(() => {
    if (selectedRows.length === 0 || exporting) return;
    setExporting(true);

    // Use double-rAF to guarantee the browser has painted the disabled/spinner
    // state before kicking off the export RPC.
    requestAnimationFrame(() => {
      requestAnimationFrame(() => {
        const run = async () => {
          const baseDir = await resolveBaseDir();
          if (baseDir === null) {
            setExporting(false);
            return;
          }

          setExportProgress({ done: 0, total: selectedRows.length });

          const paths: string[] = [];
          try {
            for (let i = 0; i < selectedRows.length; i++) {
              const row = selectedRows[i]!;
              const result = await assetsExport({
                assetName: row.name ?? "unnamed",
                collection: row.collection ?? "",
                pathId: row.pathId,
                kind: row.className ?? "",
                baseDir,
              });
              paths.push(result.outputPath);
              setExportProgress({ done: i + 1, total: selectedRows.length });
            }
            const first = paths[0];
            pushToast({
              variant: "success",
              message: `Exported ${paths.length} asset${paths.length === 1 ? "" : "s"}`,
              ...(first != null ? { detail: first } : {}),
              ...(first != null ? { actions: [{ label: "Reveal", run: () => void revealInExplorer(first) }] } : {}),
            });
          } catch (err) {
            pushToast({
              variant: "error",
              message: `Export failed: ${(err as Error).message}`,
            });
          } finally {
            setExporting(false);
            setExportProgress(null);
          }
        };

        void run();
      });
    });
  }, [selectedRows, exporting, resolveBaseDir, pushToast]);

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
    setDestination("project");
  }, [projectPath]);

  if (status === null) {
    return (
      <div className={styles.root}>
        <div className={styles.gate}>
          <div className={styles.spinner} />
        </div>
      </div>
    );
  }

  if (status.state !== "current") {
    const missing = status.state === "missing";
    const stale = status.state === "stale";
    const noGame = status.state === "noGame";
    return (
      <div className={styles.root}>
        <div className={styles.gate}>
          <p className={styles.gateTitle}>
            {noGame
              ? "Game path not configured"
              : missing
                ? "No asset index yet"
                : "Asset index is out of date"}
          </p>
          {status.reason && <p className={styles.gateReason}>{status.reason}</p>}
          {indexError && <p className={styles.gateReason}>{indexError}</p>}
          {!noGame && (
            <button
              type="button"
              className={styles.gateButton}
              disabled={indexing}
              onClick={() => void handleIndex()}
            >
              {indexing && <span className={styles.gateSpinner} />}
              {indexing ? "Indexing…" : stale ? "Re-index" : "Index assets"}
            </button>
          )}
        </div>
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

      <div className={styles.body}>
        <div className={styles.listPanel}>
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
          <div className={styles.list}>
            {loadingAssets ? (
              <div className={styles.detailsEmpty}>Loading index…</div>
            ) : results.length === 0 ? (
              <div className={styles.detailsEmpty}>
                {query || kindFilter !== "all"
                  ? "No matches"
                  : `${status.assetCount ?? 0} assets indexed. Start typing to search.`}
              </div>
            ) : (
              results.map((row) => {
                const key = rowKey(row);
                const selected = selection.has(key);
                const focused = focusedKey === key;
                const group = classifyAsset(row.className);
                return (
                  <button
                    key={key}
                    type="button"
                    className={`${styles.row} ${selected ? styles.rowSelected : ""} ${focused ? styles.rowFocused : ""}`}
                    onClick={(e) => handleRowClick(row, e)}
                  >
                    <span
                      className={styles.rowCheck}
                      onClick={(e) => {
                        e.stopPropagation();
                        toggleSelection(key);
                      }}
                    >
                      <input type="checkbox" checked={selected} readOnly />
                    </span>
                    <span className={styles.rowName}>{row.name ?? "(unnamed)"}</span>
                    <span className={styles.rowKind}>
                      {group ? ASSET_KIND_GROUP_LABEL[group] : (row.className ?? "—")}
                    </span>
                  </button>
                );
              })
            )}
          </div>
        </div>

        <div className={styles.detailsPanel}>
          {focusedKey === null ? (
            <div className={styles.detailsEmpty}>Select an asset to see details</div>
          ) : (
            (() => {
              const focused = results.find((r) => rowKey(r) === focusedKey);
              if (!focused) return <div className={styles.detailsEmpty}>—</div>;
              return (
                <>
                  <div className={styles.preview}>
                    {previewData?.mimeType === "image/png" ? (
                      <img
                        className={styles.previewImage}
                        src={previewData.dataUrl}
                        alt={focused.name ?? "Asset preview"}
                      />
                    ) : previewData?.mimeType.startsWith("audio/") ? (
                      <div className={styles.audioPreview}>
                        {/* eslint-disable-next-line jsx-a11y/media-has-caption */}
                        <audio
                          className={styles.audioPlayer}
                          controls
                          src={previewData.dataUrl}
                        />
                      </div>
                    ) : previewData?.mimeType === "model/gltf-binary" ? (
                      <ModelViewer dataUrl={previewData.dataUrl} />
                    ) : previewLoading ? (
                      <span className={styles.previewSpinner} />
                    ) : (
                      <span className={styles.previewPlaceholder}>No preview</span>
                    )}
                  </div>
                  <div className={styles.meta}>
                    <MetaRow label="Name" value={focused.name ?? "—"} />
                    <MetaRow label="Class" value={focused.className ?? "—"} />
                    <MetaRow label="Collection" value={focused.collection ?? "—"} />
                    <MetaRow label="Path ID" value={String(focused.pathId)} />
                    {focused.audioFrequency != null && (
                      <MetaRow label="Frequency" value={`${focused.audioFrequency} Hz`} />
                    )}
                    {focused.audioChannels != null && (
                      <MetaRow label="Channels" value={String(focused.audioChannels)} />
                    )}
                    {focused.spriteBackingTextureName && (
                      <MetaRow label="Atlas" value={focused.spriteBackingTextureName} />
                    )}
                  </div>
                </>
              );
            })()
          )}
          <div className={styles.exportBar}>
            <div className={styles.exportOptions}>
              <label className={styles.exportOption}>
                <input
                  type="radio"
                  name="asset-export-destination"
                  checked={destination === "default"}
                  onChange={() => setDestination("default")}
                />
                <span>Default</span>
                <span className={styles.exportOptionPath}>.jiangyu/exports</span>
              </label>
              <label className={styles.exportOption}>
                <input
                  type="radio"
                  name="asset-export-destination"
                  checked={destination === "project"}
                  onChange={() => setDestination("project")}
                  disabled={projectExportPath === null}
                />
                <span>Project</span>
                <span className={styles.exportOptionPath}>
                  {projectExportPath ?? "not configured"}
                </span>
                <button
                  type="button"
                  className={styles.exportProjectConfigure}
                  onClick={() => void handleConfigureProjectPath()}
                >
                  {projectExportPath ? "change" : "configure"}
                </button>
              </label>
              <label className={styles.exportOption}>
                <input
                  type="radio"
                  name="asset-export-destination"
                  checked={destination === "custom"}
                  onChange={() => setDestination("custom")}
                />
                <span>Custom…</span>
              </label>
            </div>
            <div className={styles.exportActions}>
              <button
                type="button"
                className={styles.exportButton}
                disabled={selectedRows.length === 0 || exporting}
                onClick={handleExport}
              >
                {exporting && <span className={styles.exportSpinner} />}
                {exporting ? "Exporting…" : `Export ${selectedRows.length || "0"} selected`}
              </button>
              {exportProgress && exporting && exportProgress.total > 1 && (
                <div className={styles.exportProgress}>
                  <div
                    className={styles.exportProgressFill}
                    style={{ width: `${(exportProgress.done / exportProgress.total) * 100}%` }}
                  />
                </div>
              )}
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}

function MetaRow({ label, value }: { label: string; value: string }) {
  return (
    <div className={styles.metaRow}>
      <span className={styles.metaLabel}>{label}</span>
      <span className={styles.metaValue}>{value}</span>
    </div>
  );
}
