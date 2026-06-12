import uFuzzy from "@leeoniya/ufuzzy";
import { useVirtualizer } from "@tanstack/react-virtual";
import { GripVertical } from "lucide-react";
import { memo, useCallback, useDeferredValue, useEffect, useMemo, useRef, useState } from "react";
import { rpcCall } from "@shared/rpc";
import type {
  InspectedFieldNode,
  ObjectInspectionResult,
  TemplateIndexStatus,
  TemplateInstanceEntry,
  TemplateQueryResult,
  TemplateReferenceEntry,
  TemplateSearchResult,
  TemplateTypeEntry,
} from "@shared/rpc";
import { attachDragChip } from "@shared/drag/chip";
import { generatePatchKdl, generateCloneKdl } from "./kdlSnippets";
import {
  encodeCrossInstancePayload,
  TEMPLATE_DRAG_TAG,
  INSTANCE_DRAG_TAG,
  beginTemplateDrag,
  endTemplateDrag,
} from "@features/templates/crossInstance";
import { useToastPush } from "@shared/toast";
import {
  DEFAULT_TEMPLATE_BROWSER_STATE,
  type TemplateBrowserState,
} from "@features/panes/browserState";
import { useDebouncedScrollTop } from "@shared/utils/useDebouncedScrollTop";
import { useDismissOnOutsideClick } from "@shared/utils/useDismissOnOutsideClick";
import { onKeyActivate } from "@shared/utils/a11y";
import { Spinner } from "@shared/ui/Spinner/Spinner";
import { LoadingBanner } from "@shared/ui/LoadingBanner/LoadingBanner";
import { EmptyState } from "@shared/ui/EmptyState/EmptyState";
import { Button } from "@shared/ui/Button/Button";
import {
  TemplateFilePicker,
  type PickerResult,
} from "@features/templates/TemplateFilePicker/TemplateFilePicker";
import styles from "./TemplateBrowser.module.css";
import { TemplateDetail } from "./DetailPanel";
import {
  buildReferenceTargetIndex,
  instanceKey,
  navStepBack,
  navStepForward,
  pushNavEntry,
} from "./helpers";

function templatesIndexStatus(): Promise<TemplateIndexStatus> {
  return rpcCall<TemplateIndexStatus>("templatesIndexStatus");
}

function templatesIndex(): Promise<TemplateIndexStatus> {
  return rpcCall<TemplateIndexStatus>("templatesIndex", undefined, { timeoutMs: 0 });
}

function templatesSearch(): Promise<TemplateSearchResult> {
  return rpcCall<TemplateSearchResult>("templatesSearch");
}

function templatesQuery(
  typeName: string,
  fieldPath?: string,
  namespaceName?: string | null,
): Promise<TemplateQueryResult> {
  return rpcCall<TemplateQueryResult>("templatesQuery", { typeName, fieldPath, namespaceName });
}

function templatesInspect(params: {
  collection: string;
  pathId: number;
  maxDepth?: number;
  maxArraySample?: number;
}): Promise<ObjectInspectionResult> {
  return rpcCall<ObjectInspectionResult>("templatesInspect", params);
}

// --- Helpers ---
// Pure helpers (instanceKey, nav history, enum leaf resolution) live in
// `./helpers` so this JSX module only exports React components.

const uf = new uFuzzy({});
const ROW_HEIGHT = 28;

// Stable empty list for the detail panel's referencedBy prop, so the
// memoised TemplateDetail doesn't see a fresh [] identity per render.
const NO_REFERENCES: readonly TemplateReferenceEntry[] = [];

// --- Component ---

interface TemplateBrowserProps {
  projectPath: string;
  fileEntries: readonly string[];
  lastCodePath: string | null;
  onOpenFile: (path: string) => void;
  onAppendToFile: (path: string, snippet: string) => Promise<void>;
  onRefreshFiles: () => void;
  initialState?: TemplateBrowserState | undefined;
  onStateChange?: ((state: TemplateBrowserState) => void) | undefined;
}

export function TemplateBrowser({
  projectPath,
  fileEntries,
  lastCodePath,
  onOpenFile,
  onAppendToFile,
  onRefreshFiles,
  initialState,
  onStateChange,
}: TemplateBrowserProps) {
  const [status, setStatus] = useState<TemplateIndexStatus | null>(null);
  const [indexing, setIndexing] = useState(false);
  const [indexError, setIndexError] = useState<string | null>(null);

  const [allTypes, setAllTypes] = useState<readonly TemplateTypeEntry[]>([]);
  const [allInstances, setAllInstances] = useState<readonly TemplateInstanceEntry[]>([]);
  const [referencedBy, setReferencedBy] = useState<
    Readonly<Record<string, readonly TemplateReferenceEntry[]>>
  >({});
  const [loadingData, setLoadingData] = useState(false);

  // Controlled via initialState / onStateChange so parent can persist and
  // transfer. Initialiser pattern (useState with lazy init) avoids re-seeding
  // from a later prop change.
  const [query, setQuery] = useState(
    () => initialState?.query ?? DEFAULT_TEMPLATE_BROWSER_STATE.query,
  );
  const [typeFilter, setTypeFilter] = useState(
    () => initialState?.typeFilter ?? DEFAULT_TEMPLATE_BROWSER_STATE.typeFilter,
  );
  const [focusedKey, setFocusedKey] = useState<string | null>(
    () => initialState?.focusedKey ?? DEFAULT_TEMPLATE_BROWSER_STATE.focusedKey,
  );

  // Navigation history for detail panel
  const [navHistory, setNavHistory] = useState<string[]>(() => [
    ...(initialState?.navHistory ?? DEFAULT_TEMPLATE_BROWSER_STATE.navHistory),
  ]);
  const [navIndex, setNavIndex] = useState(
    () => initialState?.navIndex ?? DEFAULT_TEMPLATE_BROWSER_STATE.navIndex,
  );
  const canGoBack = navIndex > 0;
  const canGoForward = navIndex < navHistory.length - 1;

  const pushNav = useCallback(
    (key: string) => {
      const next = pushNavEntry(navHistory, navIndex, key);
      setNavHistory(next.history);
      setNavIndex(next.index);
    },
    [navHistory, navIndex],
  );

  const goBack = useCallback(() => {
    const step = navStepBack(navHistory, navIndex);
    if (!step) return;
    setNavIndex(step.index);
    setFocusedKey(step.key);
  }, [navHistory, navIndex]);

  const goForward = useCallback(() => {
    const step = navStepForward(navHistory, navIndex);
    if (!step) return;
    setNavIndex(step.index);
    setFocusedKey(step.key);
  }, [navHistory, navIndex]);

  const [memberData, setMemberData] = useState<TemplateQueryResult | null>(null);
  const [membersLoading, setMembersLoading] = useState(false);
  const memberTokenRef = useRef(0);

  // Inspection values
  const [inspectionValues, setInspectionValues] = useState<readonly InspectedFieldNode[] | null>(
    null,
  );
  const [inspectionLoading, setInspectionLoading] = useState(false);

  // Resizable split
  const [listFraction, setListFraction] = useState(
    () => initialState?.listFraction ?? DEFAULT_TEMPLATE_BROWSER_STATE.listFraction,
  );
  const bodyRef = useRef<HTMLDivElement>(null);

  const [scrollTop, handleListScroll] = useDebouncedScrollTop(
    initialState?.scrollTop ?? DEFAULT_TEMPLATE_BROWSER_STATE.scrollTop,
  );

  const onStateChangeRef = useRef(onStateChange);
  useEffect(() => {
    onStateChangeRef.current = onStateChange;
  }, [onStateChange]);
  useEffect(() => {
    onStateChangeRef.current?.({
      query,
      typeFilter,
      focusedKey,
      navHistory,
      navIndex,
      listFraction,
      scrollTop,
    });
  }, [query, typeFilter, focusedKey, navHistory, navIndex, listFraction, scrollTop]);

  const pushToast = useToastPush();

  // --- Index status ---
  useEffect(() => {
    let cancelled = false;
    void templatesIndexStatus()
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

  // Reset catalogue state synchronously off status state.
  const [prevStatusState, setPrevStatusState] = useState(status?.state);
  if (prevStatusState !== status?.state) {
    setPrevStatusState(status?.state);
    if (status?.state === "current") {
      setLoadingData(true);
    } else {
      setAllTypes([]);
      setAllInstances([]);
      setReferencedBy({});
    }
  }

  // --- Load full index ---
  useEffect(() => {
    if (status?.state !== "current") return;
    let cancelled = false;
    void templatesSearch()
      .then((result) => {
        if (cancelled) return;
        setAllTypes(result.types);
        setAllInstances(result.instances);
        setReferencedBy(result.referencedBy ?? {});
      })
      .catch((err: unknown) => {
        console.error("[TemplateBrowser] search failed:", err);
        if (!cancelled) {
          setAllTypes([]);
          setAllInstances([]);
          setReferencedBy({});
        }
      })
      .finally(() => {
        if (!cancelled) setLoadingData(false);
      });
    return () => {
      cancelled = true;
    };
  }, [status?.state]);

  // --- Instance lookup for resolving reference identities ---
  const instanceLookup = useMemo(() => {
    const map = new Map<string, TemplateInstanceEntry>();
    for (const inst of allInstances) {
      map.set(instanceKey(inst), inst);
    }
    return map;
  }, [allInstances]);

  // Secondary index so the detail panel resolves inspected references
  // (pathId + optional name) in O(1) instead of scanning every instance
  // per rendered reference link.
  const referenceTargetIndex = useMemo(
    () => buildReferenceTargetIndex(allInstances),
    [allInstances],
  );

  // --- Fuzzy search ---
  const deferredQuery = useDeferredValue(query);

  const filtered = useMemo(() => {
    let items = allInstances;

    if (typeFilter !== "all") {
      items = items.filter((inst) => inst.className === typeFilter);
    }

    if (deferredQuery.trim().length === 0) return items;

    const filteredHaystack = items.map((inst) => `${inst.className} ${inst.name}`);
    const [idxs] = uf.search(filteredHaystack, deferredQuery);
    if (!idxs) return [];
    return idxs
      .map((i) => items[i])
      .filter((inst): inst is TemplateInstanceEntry => inst !== undefined);
  }, [allInstances, deferredQuery, typeFilter]);

  // --- Load members when focus changes ---
  const focusedInstance = useMemo(
    () => (focusedKey ? (instanceLookup.get(focusedKey) ?? null) : null),
    [focusedKey, instanceLookup],
  );

  // Reset member/inspection state synchronously off focusedInstance identity
  // so the panel clears the moment focus moves rather than one render later.
  const [prevFocusedInstance, setPrevFocusedInstance] = useState(focusedInstance);
  if (prevFocusedInstance !== focusedInstance) {
    setPrevFocusedInstance(focusedInstance);
    setMemberData(null);
    setInspectionValues(null);
    setMembersLoading(focusedInstance !== null);
    setInspectionLoading(focusedInstance !== null);
  }

  useEffect(() => {
    if (!focusedInstance) return;
    const token = ++memberTokenRef.current;
    const { className, namespaceName, identity } = focusedInstance;

    // Fetch schema (existing)
    void templatesQuery(className, undefined, namespaceName)
      .then((result) => {
        if (memberTokenRef.current !== token) return;
        setMemberData(result);
      })
      .catch((err: unknown) => {
        console.warn("[TemplateBrowser] query failed:", err);
      })
      .finally(() => {
        if (memberTokenRef.current === token) setMembersLoading(false);
      });

    // Fetch serialised values (new)
    // Depth 6: m_Structure (2), top-level fields (3), array elements (4), element fields (5), nested fields (6).
    void templatesInspect({
      collection: identity.collection,
      pathId: identity.pathId,
      maxDepth: 6,
      maxArraySample: 0, // unlimited
    })
      .then((result) => {
        if (memberTokenRef.current !== token) return;
        // Extract m_Structure fields — the actual template data payload
        const structure = result.fields.find((f) => f.name === "m_Structure");
        setInspectionValues(structure?.fields ?? result.fields);
      })
      .catch((err: unknown) => {
        console.warn("[TemplateBrowser] inspect failed:", err);
      })
      .finally(() => {
        if (memberTokenRef.current === token) setInspectionLoading(false);
      });
  }, [focusedInstance]);

  // --- Build index ---
  const handleBuildIndex = useCallback(async () => {
    setIndexing(true);
    setIndexError(null);
    try {
      const result = await templatesIndex();
      setStatus(result);
      pushToast({ variant: "success", message: "Template index built" });
    } catch (err) {
      const msg = (err as Error).message;
      setIndexError(msg);
      pushToast({ variant: "error", message: "Index failed", detail: msg });
    } finally {
      setIndexing(false);
    }
  }, [pushToast]);

  // --- Scaffold actions ---

  const templateFiles = useMemo(
    () => fileEntries.filter((f) => f.startsWith("templates/") && f.endsWith(".kdl")),
    [fileEntries],
  );

  const isActiveFileTemplate =
    lastCodePath !== null &&
    lastCodePath.startsWith(projectPath + "/templates/") &&
    lastCodePath.endsWith(".kdl");

  const [pickerOpen, setPickerOpen] = useState(false);
  const [pendingSnippet, setPendingSnippet] = useState<string | null>(null);

  const createFileWithSnippet = useCallback(
    async (filePath: string, snippet: string) => {
      const dir = filePath.substring(0, filePath.lastIndexOf("/"));
      try {
        await rpcCall<null>("createDirectory", { path: dir });
      } catch {
        /* exists */
      }
      await rpcCall<null>("writeFile", { path: filePath, content: snippet });
      onRefreshFiles();
      onOpenFile(filePath);
      pushToast({
        variant: "success",
        message: "File created",
        detail: filePath,
        actions: [{ label: "View", run: () => onOpenFile(filePath) }],
      });
    },
    [onOpenFile, onRefreshFiles, pushToast],
  );

  const appendSnippetToFile = useCallback(
    async (filePath: string, snippet: string) => {
      try {
        await onAppendToFile(filePath, snippet);
        onOpenFile(filePath);
        pushToast({
          variant: "success",
          message: "Snippet added",
          detail: filePath,
          actions: [{ label: "View", run: () => onOpenFile(filePath) }],
        });
      } catch (err) {
        pushToast({
          variant: "error",
          message: "Append failed",
          detail: (err as Error).message,
        });
      }
    },
    [onAppendToFile, onOpenFile, pushToast],
  );

  const handlePickerResult = useCallback(
    async (result: PickerResult) => {
      setPickerOpen(false);
      const snippet = pendingSnippet;
      if (!snippet) return;
      setPendingSnippet(null);

      if (result.kind === "existing") {
        await appendSnippetToFile(result.path, snippet);
      } else {
        const filePath = `${projectPath}/templates/${result.filename}`;
        await createFileWithSnippet(filePath, snippet);
      }
    },
    [appendSnippetToFile, createFileWithSnippet, pendingSnippet, projectPath],
  );

  const openPickerWithSnippet = useCallback((snippet: string) => {
    setPendingSnippet(snippet);
    setPickerOpen(true);
  }, []);

  const handleSmartDefault = useCallback(
    async (snippet: string) => {
      if (isActiveFileTemplate && lastCodePath) {
        await appendSnippetToFile(lastCodePath, snippet);
      } else if (templateFiles.length > 0) {
        openPickerWithSnippet(snippet);
      } else {
        // No template files — open picker in "create new" mode
        openPickerWithSnippet(snippet);
      }
    },
    [
      isActiveFileTemplate,
      lastCodePath,
      templateFiles.length,
      appendSnippetToFile,
      openPickerWithSnippet,
    ],
  );

  const handleCreatePatch = useCallback(
    (inst?: TemplateInstanceEntry) => {
      const target = inst ?? focusedInstance;
      if (!target) return;
      const snippet = generatePatchKdl(target.className, target.name);
      void handleSmartDefault(snippet);
    },
    [focusedInstance, handleSmartDefault],
  );

  const handleCreateClone = useCallback(
    (inst?: TemplateInstanceEntry) => {
      const target = inst ?? focusedInstance;
      if (!target) return;
      const cloneId = `${target.name}_clone`;
      const snippet = generateCloneKdl(target.className, target.name, cloneId);
      void handleSmartDefault(snippet);
    },
    [focusedInstance, handleSmartDefault],
  );

  const handlePatchToFile = useCallback(
    (inst?: TemplateInstanceEntry) => {
      const target = inst ?? focusedInstance;
      if (!target) return;
      openPickerWithSnippet(generatePatchKdl(target.className, target.name));
    },
    [focusedInstance, openPickerWithSnippet],
  );

  const handleCloneToFile = useCallback(
    (inst?: TemplateInstanceEntry) => {
      const target = inst ?? focusedInstance;
      if (!target) return;
      const cloneId = `${target.name}_clone`;
      openPickerWithSnippet(generateCloneKdl(target.className, target.name, cloneId));
    },
    [focusedInstance, openPickerWithSnippet],
  );

  // --- Navigate to a template (from reference tree) ---
  const navigateTo = useCallback(
    (key: string) => {
      setFocusedKey(key);
      pushNav(key);
      const inst = instanceLookup.get(key);
      if (inst && typeFilter !== "all" && inst.className !== typeFilter) {
        setTypeFilter("all");
      }
    },
    [instanceLookup, typeFilter, pushNav],
  );

  // Stable callback for row selection. Each InstanceRow passes its own key
  // in at call time, so a single closure serves all rows; no per-row arrow
  // means the memo'd InstanceRow doesn't bail on prop-identity changes.
  const handleInstanceSelect = useCallback(
    (key: string) => {
      setFocusedKey(key);
      pushNav(key);
    },
    [pushNav],
  );

  // --- Virtualiser ---
  const listRef = useRef<HTMLDivElement>(null);
  // eslint-disable-next-line react-hooks/incompatible-library -- TanStack Virtual returns non-memoisable functions; the only API the library exposes.
  const rowVirtualizer = useVirtualizer({
    count: filtered.length,
    getScrollElement: () => listRef.current,
    estimateSize: () => ROW_HEIGHT,
    overscan: 20,
  });

  // Restore scroll position once the list is populated.
  const scrollRestoredRef = useRef(false);
  useEffect(() => {
    if (scrollRestoredRef.current) return;
    if (filtered.length === 0) return;
    const target = initialState?.scrollTop ?? 0;
    if (target > 0 && listRef.current !== null) {
      listRef.current.scrollTop = target;
    }
    scrollRestoredRef.current = true;
  }, [filtered, initialState]);

  // --- Split handle drag ---
  // During the drag the fraction is written straight to the two panels'
  // flex styles, so pointermove never re-renders the (virtualised, heavy)
  // tree; React state commits once on pointerup for persistence.
  const listPanelRef = useRef<HTMLDivElement>(null);
  const detailPanelRef = useRef<HTMLDivElement>(null);
  const handleSplitPointerDown = useCallback(
    (e: React.PointerEvent) => {
      e.preventDefault();
      const el = bodyRef.current;
      if (!el) return;
      const start = e.clientX;
      const startFrac = listFraction;
      const totalWidth = el.getBoundingClientRect().width;
      let frac = startFrac;

      const onMove = (ev: PointerEvent) => {
        const dx = ev.clientX - start;
        frac = Math.min(0.85, Math.max(0.15, startFrac + dx / totalWidth));
        if (listPanelRef.current) listPanelRef.current.style.flex = String(frac);
        if (detailPanelRef.current) detailPanelRef.current.style.flex = String(1 - frac);
      };
      const onUp = () => {
        document.removeEventListener("pointermove", onMove);
        document.removeEventListener("pointerup", onUp);
        setListFraction(frac);
      };
      document.addEventListener("pointermove", onMove);
      document.addEventListener("pointerup", onUp);
    },
    [listFraction],
  );

  // --- Render ---

  // Index not ready
  if (status === null) {
    return (
      <div className={styles.root}>
        <LoadingBanner label="Checking template index…" />
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
        ? "No template index yet"
        : "Template index is out of date";
    const reasons = [status.reason, indexError].filter((r): r is string => Boolean(r));
    return (
      <div className={styles.root}>
        <EmptyState
          title={title}
          reason={reasons}
          action={
            !noGame && (
              <Button variant="primary" disabled={indexing} onClick={() => void handleBuildIndex()}>
                {indexing && (
                  <Spinner
                    size={12}
                    trackColor="var(--track-on-accent)"
                    accentColor="var(--paper-0)"
                  />
                )}
                {indexing ? "Indexing…" : stale ? "Re-index" : "Index templates"}
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
          type="text"
          className={styles.search}
          placeholder="Search templates…"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
        />
        <TypeCombobox
          types={allTypes}
          totalCount={allInstances.length}
          value={typeFilter}
          onChange={(val) => {
            setTypeFilter(val);
            setFocusedKey(null);
          }}
        />
      </div>

      {loadingData ? (
        <LoadingBanner label="Loading templates…" />
      ) : (
        <div ref={bodyRef} className={styles.body}>
          {/* --- List panel --- */}
          <div ref={listPanelRef} className={styles.listPanel} style={{ flex: listFraction }}>
            <div ref={listRef} className={styles.listScroll} onScroll={handleListScroll}>
              <div
                style={{
                  height: `${rowVirtualizer.getTotalSize()}px`,
                  position: "relative",
                }}
              >
                {rowVirtualizer.getVirtualItems().map((vRow) => {
                  const inst = filtered[vRow.index];
                  if (inst === undefined) return null;
                  const key = instanceKey(inst);
                  const focused = key === focusedKey;
                  const refs = referencedBy[key];
                  const ownerInst = refs?.[0]
                    ? instanceLookup.get(`${refs[0].source.collection}:${refs[0].source.pathId}`)
                    : undefined;
                  return (
                    <InstanceRow
                      key={key}
                      instanceKey={key}
                      inst={inst}
                      ownerInst={ownerInst}
                      focused={focused}
                      virtualSize={vRow.size}
                      virtualStart={vRow.start}
                      onSelect={handleInstanceSelect}
                    />
                  );
                })}
              </div>
            </div>
            <div className={styles.listFooter}>
              {filtered.length.toLocaleString()} / {allInstances.length.toLocaleString()} templates
            </div>
          </div>

          {/* --- Split handle --- */}
          <div className={styles.splitHandle} onPointerDown={handleSplitPointerDown} />

          {/* --- Detail panel --- */}
          <div
            ref={detailPanelRef}
            className={styles.detailPanel}
            style={{ flex: 1 - listFraction }}
          >
            {focusedInstance ? (
              <TemplateDetail
                instance={focusedInstance}
                memberData={memberData}
                membersLoading={membersLoading}
                inspectionValues={inspectionValues}
                inspectionLoading={inspectionLoading}
                onCreatePatch={handleCreatePatch}
                onCreateClone={handleCreateClone}
                onPatchToFile={handlePatchToFile}
                onCloneToFile={handleCloneToFile}
                onNavigate={navigateTo}
                onGoBack={goBack}
                onGoForward={goForward}
                canGoBack={canGoBack}
                canGoForward={canGoForward}
                projectPath={projectPath}
                referencedBy={
                  (focusedKey !== null ? referencedBy[focusedKey] : undefined) ?? NO_REFERENCES
                }
                instanceLookup={instanceLookup}
                referenceTargetIndex={referenceTargetIndex}
                allReferencedBy={referencedBy}
              />
            ) : (
              <div className={styles.emptyDetail}>
                <span>Select a template to inspect</span>
              </div>
            )}
          </div>
        </div>
      )}
      {pickerOpen && (
        <TemplateFilePicker
          templateFiles={templateFiles}
          projectPath={projectPath}
          onSelect={(result) => void handlePickerResult(result)}
          onCancel={() => {
            setPickerOpen(false);
            setPendingSnippet(null);
          }}
        />
      )}
    </div>
  );
}

// --- Instance row ---

interface InstanceRowProps {
  /** Stable key passed back into onSelect so a single shared callback can
   *  handle every row; keeps onSelect prop-identity-stable for memo. */
  instanceKey: string;
  inst: TemplateInstanceEntry;
  ownerInst: TemplateInstanceEntry | undefined;
  focused: boolean;
  virtualSize: number;
  virtualStart: number;
  onSelect: (key: string) => void;
}

/// Instance row in the list panel — virtualised, draggable into the visual
/// editor. Pulled into its own component so each row can hold its own drag
/// state and render the hover-revealed grip glyph without lifting that state
/// to the (already-busy) parent component.
///
/// Memoised because the browser hosts thousands of rows: a single parent
/// re-render (search input, focus change, fetched-data update) would
/// otherwise force every visible row to re-render even when its own props
/// haven't changed. Coupled with the parent's stable handleInstanceSelect
/// callback (which takes the row's key at call time), the only props that
/// shift during scroll are virtualStart/virtualSize on visible rows.
const InstanceRow = memo(function InstanceRow({
  instanceKey,
  inst,
  ownerInst,
  focused,
  virtualSize,
  virtualStart,
  onSelect,
}: InstanceRowProps) {
  const [dragging, setDragging] = useState(false);
  const handleClick = useCallback(() => onSelect(instanceKey), [onSelect, instanceKey]);
  return (
    <div
      className={`${styles.row} ${focused ? styles.rowFocused : ""} ${dragging ? styles.rowDragging : ""}`}
      title={ownerInst ? `${inst.name} ← ${ownerInst.name}` : inst.name}
      role="button"
      tabIndex={0}
      draggable
      onDragStart={(e) => {
        e.dataTransfer.effectAllowed = "copy";
        e.dataTransfer.setData(
          "text/plain",
          encodeCrossInstancePayload({ name: inst.name, className: inst.className }),
        );
        e.dataTransfer.setData(TEMPLATE_DRAG_TAG, "1");
        e.dataTransfer.setData(INSTANCE_DRAG_TAG, "1");
        // The virtualised row is `position: absolute` over a sparse parent,
        // and the browser-default drag image of that ends up clipped /
        // invisible under WebKitGTK. Use the design-system chip helper that
        // tab/pane drags already use — keeps the floating affordance visible
        // and consistent across every drag source.
        attachDragChip(e, inst.name);
        beginTemplateDrag({
          kind: "instance",
          name: inst.name,
          className: inst.className,
        });
        setDragging(true);
      }}
      onDragEnd={() => {
        setDragging(false);
        endTemplateDrag();
      }}
      style={{
        position: "absolute",
        top: 0,
        left: 0,
        width: "100%",
        height: `${virtualSize}px`,
        transform: `translateY(${virtualStart}px)`,
      }}
      onClick={handleClick}
      onKeyDown={onKeyActivate(handleClick)}
    >
      <span className={styles.rowDragGrip} aria-hidden>
        <GripVertical size={10} />
      </span>
      <span className={styles.rowName}>{inst.name}</span>
      {ownerInst && <span className={styles.rowOwner}>← {ownerInst.name}</span>}
    </div>
  );
});

// --- Type combobox ---

interface TypeComboboxProps {
  types: readonly TemplateTypeEntry[];
  totalCount: number;
  value: string;
  onChange: (value: string) => void;
}

function TypeCombobox({ types, totalCount, value, onChange }: TypeComboboxProps) {
  const [open, setOpen] = useState(false);
  const [filter, setFilter] = useState("");
  const [highlightIdx, setHighlightIdx] = useState(0);
  const rootRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  const listRef = useRef<HTMLDivElement>(null);

  const filtered = useMemo(() => {
    if (filter.trim().length === 0) return types;
    const lower = filter.toLowerCase();
    return types.filter((t) => t.className.toLowerCase().includes(lower));
  }, [types, filter]);

  // Reset highlight when filter changes
  const [prevFilteredLen, setPrevFilteredLen] = useState(filtered.length);
  if (prevFilteredLen !== filtered.length) {
    setPrevFilteredLen(filtered.length);
    setHighlightIdx(0);
  }

  // Close on outside click
  useDismissOnOutsideClick(rootRef, () => setOpen(false), { enabled: open });

  // Scroll highlighted item into view
  useEffect(() => {
    if (!open || !listRef.current) return;
    const item = listRef.current.children[highlightIdx] as HTMLElement | undefined;
    item?.scrollIntoView({ block: "nearest" });
  }, [open, highlightIdx]);

  const select = useCallback(
    (val: string) => {
      onChange(val);
      setOpen(false);
      setFilter("");
    },
    [onChange],
  );

  const displayLabel =
    value === "all"
      ? `All types (${totalCount})`
      : `${value} (${types.find((t) => t.className === value)?.count ?? 0})`;

  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent) => {
      // +1 for the "All types" row at the top
      const itemCount = filtered.length + 1;

      if (e.key === "ArrowDown") {
        e.preventDefault();
        if (!open) {
          setOpen(true);
        } else {
          setHighlightIdx((prev) => Math.min(prev + 1, itemCount - 1));
        }
      } else if (e.key === "ArrowUp") {
        e.preventDefault();
        setHighlightIdx((prev) => Math.max(prev - 1, 0));
      } else if (e.key === "Enter") {
        e.preventDefault();
        if (!open) {
          setOpen(true);
        } else {
          const val = highlightIdx === 0 ? "all" : (filtered[highlightIdx - 1]?.className ?? "all");
          select(val);
        }
      } else if (e.key === "Escape") {
        setOpen(false);
        setFilter("");
      }
    },
    [open, filtered, highlightIdx, select],
  );

  return (
    <div ref={rootRef} className={styles.comboRoot}>
      {open ? (
        <input
          ref={inputRef}
          className={styles.comboInput}
          placeholder="Filter types…"
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
          onKeyDown={handleKeyDown}
          // eslint-disable-next-line jsx-a11y/no-autofocus
          autoFocus
        />
      ) : (
        <button
          type="button"
          className={styles.comboTrigger}
          onClick={() => setOpen(true)}
          onKeyDown={handleKeyDown}
        >
          {displayLabel}
        </button>
      )}
      {open && (
        <div ref={listRef} className={styles.comboList} role="listbox">
          <div
            role="option"
            aria-selected={value === "all"}
            className={`${styles.comboOption} ${highlightIdx === 0 ? styles.comboOptionHighlight : ""} ${value === "all" ? styles.comboOptionActive : ""}`}
            onPointerDown={() => select("all")}
          >
            <span className={styles.comboOptionLabel}>All types</span>
            <span className={styles.comboOptionCount}>{totalCount}</span>
          </div>
          {filtered.map((t, i) => {
            const idx = i + 1;
            const active = value === t.className;
            return (
              <div
                key={t.className}
                role="option"
                aria-selected={active}
                className={`${styles.comboOption} ${highlightIdx === idx ? styles.comboOptionHighlight : ""} ${active ? styles.comboOptionActive : ""}`}
                onPointerDown={() => select(t.className)}
              >
                <span className={styles.comboOptionLabel}>{t.className}</span>
                <span className={styles.comboOptionCount}>{t.count}</span>
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}
