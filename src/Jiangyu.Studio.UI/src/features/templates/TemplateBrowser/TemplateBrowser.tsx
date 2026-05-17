import uFuzzy from "@leeoniya/ufuzzy";
import { useVirtualizer } from "@tanstack/react-virtual";
import { GripVertical } from "lucide-react";
import {
  Fragment,
  memo,
  useCallback,
  useDeferredValue,
  useEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import { rpcCall } from "@shared/rpc";
import type {
  InspectedFieldNode,
  InspectedReference,
  ObjectInspectionResult,
  TemplateEdge,
  TemplateIndexStatus,
  TemplateInstanceEntry,
  TemplateMember,
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
  MEMBER_DRAG_TAG,
  beginTemplateDrag,
  endTemplateDrag,
} from "@features/templates/crossInstance";
import { encodeCrossMemberPayload } from "@features/templates/crossMember";
import { useToast } from "@shared/toast";
import {
  DEFAULT_TEMPLATE_BROWSER_STATE,
  type TemplateBrowserState,
} from "@features/panes/browserState";
import { useDebouncedScrollTop } from "@shared/utils/useDebouncedScrollTop";
import { onKeyActivate } from "@shared/utils/a11y";
import { Spinner } from "@shared/ui/Spinner/Spinner";
import { LoadingBanner } from "@shared/ui/LoadingBanner/LoadingBanner";
import { EmptyState } from "@shared/ui/EmptyState/EmptyState";
import { Button } from "@shared/ui/Button/Button";
import {
  TemplateFilePicker,
  type PickerResult,
} from "@features/templates/TemplateFilePicker/TemplateFilePicker";
import { DetailTitle, MetaBlock, MetaRow, SectionHeader } from "@shared/ui/DetailPanel/DetailPanel";
import {
  MenuList,
  MenuListBody,
  MenuItem,
  MenuItemLabel,
  MenuSeparator,
} from "@shared/ui/MenuList/MenuList";
import styles from "./TemplateBrowser.module.css";
import {
  instanceKey,
  navStepBack,
  navStepForward,
  pushNavEntry,
  resolveEnumLeafLabel,
} from "./helpers";

function templatesIndexStatus(): Promise<TemplateIndexStatus> {
  return rpcCall<TemplateIndexStatus>("templatesIndexStatus");
}

function templatesIndex(): Promise<TemplateIndexStatus> {
  return rpcCall<TemplateIndexStatus>("templatesIndex");
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

  const { push: pushToast } = useToast();

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
    () => (focusedKey ? (allInstances.find((i) => instanceKey(i) === focusedKey) ?? null) : null),
    [focusedKey, allInstances],
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
  const handleSplitPointerDown = useCallback(
    (e: React.PointerEvent) => {
      e.preventDefault();
      const el = bodyRef.current;
      if (!el) return;
      const start = e.clientX;
      const startFrac = listFraction;
      const totalWidth = el.getBoundingClientRect().width;

      const onMove = (ev: PointerEvent) => {
        const dx = ev.clientX - start;
        const newFrac = Math.min(0.85, Math.max(0.15, startFrac + dx / totalWidth));
        setListFraction(newFrac);
      };
      const onUp = () => {
        document.removeEventListener("pointermove", onMove);
        document.removeEventListener("pointerup", onUp);
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
          <div className={styles.listPanel} style={{ flex: listFraction }}>
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
          <div className={styles.detailPanel} style={{ flex: 1 - listFraction }}>
            {focusedInstance ? (
              <TemplateDetail
                instance={focusedInstance}
                memberData={memberData}
                membersLoading={membersLoading}
                inspectionValues={inspectionValues}
                inspectionLoading={inspectionLoading}
                onCreatePatch={(inst) => handleCreatePatch(inst)}
                onCreateClone={(inst) => handleCreateClone(inst)}
                onPatchToFile={(inst) => handlePatchToFile(inst)}
                onCloneToFile={(inst) => handleCloneToFile(inst)}
                onNavigate={navigateTo}
                onGoBack={goBack}
                onGoForward={goForward}
                canGoBack={canGoBack}
                canGoForward={canGoForward}
                projectPath={projectPath}
                referencedBy={(focusedKey !== null ? referencedBy[focusedKey] : null) ?? []}
                instanceLookup={instanceLookup}
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

// --- Detail sub-component ---

interface TemplateDetailProps {
  instance: TemplateInstanceEntry;
  memberData: TemplateQueryResult | null;
  membersLoading: boolean;
  inspectionValues: readonly InspectedFieldNode[] | null;
  inspectionLoading: boolean;
  onCreatePatch: (inst?: TemplateInstanceEntry) => void;
  onCreateClone: (inst?: TemplateInstanceEntry) => void;
  onPatchToFile: (inst?: TemplateInstanceEntry) => void;
  onCloneToFile: (inst?: TemplateInstanceEntry) => void;
  onNavigate: (key: string) => void;
  onGoBack: () => void;
  onGoForward: () => void;
  canGoBack: boolean;
  canGoForward: boolean;
  projectPath: string;
  referencedBy: readonly TemplateReferenceEntry[];
  instanceLookup: ReadonlyMap<string, TemplateInstanceEntry>;
  allReferencedBy: Readonly<Record<string, readonly TemplateReferenceEntry[]>>;
}

function TemplateDetail({
  instance,
  memberData,
  membersLoading,
  inspectionValues,
  inspectionLoading,
  onCreatePatch,
  onCreateClone,
  onPatchToFile,
  onCloneToFile,
  onNavigate,
  onGoBack,
  onGoForward,
  canGoBack,
  canGoForward,
  referencedBy: refs,
  instanceLookup,
  allReferencedBy,
}: TemplateDetailProps) {
  const [menuOpen, setMenuOpen] = useState(false);
  const menuRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!menuOpen) return;
    const handleClick = (e: MouseEvent) => {
      if (menuRef.current && !menuRef.current.contains(e.target as Node)) {
        setMenuOpen(false);
      }
    };
    document.addEventListener("mousedown", handleClick);
    return () => document.removeEventListener("mousedown", handleClick);
  }, [menuOpen]);

  return (
    <div className={styles.detail}>
      <div className={styles.detailNav}>
        <button
          type="button"
          className={styles.navBtn}
          disabled={!canGoBack}
          onClick={onGoBack}
          title="Back"
        >
          ←
        </button>
        <button
          type="button"
          className={styles.navBtn}
          disabled={!canGoForward}
          onClick={onGoForward}
          title="Forward"
        >
          →
        </button>
        <div className={styles.detailActions}>
          <div className={styles.scaffoldMenu} ref={menuRef}>
            <button
              type="button"
              className={styles.actionBtn}
              onClick={() => setMenuOpen((v) => !v)}
              aria-label="Scaffold"
            >
              <span>Scaffold</span>
              <span className={styles.actionBtnChevron} aria-hidden>
                ▾
              </span>
            </button>
            {menuOpen && (
              <MenuList className={styles.scaffoldMenuPos}>
                <MenuListBody>
                  <MenuItem
                    onClick={() => {
                      setMenuOpen(false);
                      onCreatePatch();
                    }}
                  >
                    <MenuItemLabel>Create Patch</MenuItemLabel>
                  </MenuItem>
                  <MenuItem
                    onClick={() => {
                      setMenuOpen(false);
                      onCreateClone();
                    }}
                  >
                    <MenuItemLabel>Create Clone</MenuItemLabel>
                  </MenuItem>
                  <MenuSeparator />
                  <MenuItem
                    onClick={() => {
                      setMenuOpen(false);
                      onPatchToFile();
                    }}
                  >
                    <MenuItemLabel>Add patch to file…</MenuItemLabel>
                  </MenuItem>
                  <MenuItem
                    onClick={() => {
                      setMenuOpen(false);
                      onCloneToFile();
                    }}
                  >
                    <MenuItemLabel>Add clone to file…</MenuItemLabel>
                  </MenuItem>
                </MenuListBody>
              </MenuList>
            )}
          </div>
        </div>
      </div>
      <div className={styles.detailHeader}>
        <DetailTitle>{instance.name}</DetailTitle>
        <MetaBlock>
          <MetaRow label="Type" value={instance.className} />
          <MetaRow label="Collection" value={instance.identity.collection} />
          <MetaRow label="PathId" value={String(instance.identity.pathId)} />
          {refs.length > 0 && (
            <MetaRow
              label="Referenced by"
              value={refs.map((r, i) => {
                const srcKey = `${r.source.collection}:${r.source.pathId}`;
                const src = instanceLookup.get(srcKey);
                const isLast = i === refs.length - 1;
                return (
                  <Fragment key={`${r.fieldName}:${srcKey}`}>
                    <span className={styles.refEntry}>
                      <button
                        type="button"
                        className={styles.refLink}
                        onClick={() => onNavigate(srcKey)}
                      >
                        {src ? `${src.name} (${src.className})` : srcKey}
                      </button>
                      {!isLast && ","}
                    </span>
                    {!isLast && " "}
                  </Fragment>
                );
              })}
            />
          )}
        </MetaBlock>
      </div>

      {/* --- Reference tree --- */}
      {instance.references && instance.references.length > 0 && (
        <div className={styles.refSection}>
          <SectionHeader>References</SectionHeader>
          <div className={styles.refTree}>
            {instance.references.map((edge) => (
              <ReferenceNode
                key={`${edge.fieldName}:${edge.target.collection}:${edge.target.pathId}`}
                edge={edge}
                depth={0}
                instanceLookup={instanceLookup}
                allReferencedBy={allReferencedBy}
                onNavigate={onNavigate}
              />
            ))}
          </div>
        </div>
      )}

      {(membersLoading ||
        inspectionLoading ||
        (memberData?.members && memberData.members.length > 0)) && (
        <div className={styles.memberSection}>
          <SectionHeader>Fields</SectionHeader>
          {(membersLoading || inspectionLoading) && !memberData?.members ? (
            <div className={styles.memberLoading}>
              <Spinner size={12} />
              <span>Loading fields…</span>
            </div>
          ) : memberData?.members ? (
            <div className={styles.memberList}>
              {memberData.members.map((m) => {
                const valueNode = inspectionValues?.find((v) => v.name === m.name) ?? null;
                return (
                  <MemberRow
                    key={m.name}
                    member={m}
                    valueNode={valueNode}
                    parentTypeName={instance.className}
                    fieldPath={m.name}
                    instanceLookup={instanceLookup}
                    onNavigate={onNavigate}
                  />
                );
              })}
            </div>
          ) : null}
        </div>
      )}
    </div>
  );
}

// --- Reference tree node ---

interface ReferenceNodeProps {
  edge: TemplateEdge;
  depth: number;
  instanceLookup: ReadonlyMap<string, TemplateInstanceEntry>;
  allReferencedBy: Readonly<Record<string, readonly TemplateReferenceEntry[]>>;
  onNavigate: (key: string) => void;
}

function ReferenceNode({
  edge,
  depth,
  instanceLookup,
  allReferencedBy,
  onNavigate,
}: ReferenceNodeProps) {
  const [expanded, setExpanded] = useState(false);
  const key = `${edge.target.collection}:${edge.target.pathId}`;
  const target = instanceLookup.get(key);
  const hasChildren = target?.references && target.references.length > 0;

  return (
    <div className={styles.refNode}>
      <div
        className={`${styles.refRow} ${hasChildren ? styles.refRowExpandable : ""}`}
        {...(hasChildren && {
          role: "button",
          tabIndex: 0,
          "aria-expanded": expanded,
          onClick: () => {
            setExpanded(!expanded);
          },
          onKeyDown: onKeyActivate(() => {
            setExpanded(!expanded);
          }),
        })}
      >
        {hasChildren ? (
          <span className={`${styles.refExpander} ${expanded ? styles.refExpanderOpen : ""}`}>
            ▸
          </span>
        ) : (
          <span className={styles.refExpanderSpacer} />
        )}
        <span className={styles.refField}>{edge.fieldName}</span>
        <button
          type="button"
          className={styles.refLink}
          onClick={(e) => {
            e.stopPropagation();
            onNavigate(key);
          }}
        >
          {target ? target.name : key}
        </button>
        {target && <span className={styles.refType}>{target.className}</span>}
      </div>
      {expanded && hasChildren && (
        <div className={styles.refChildren}>
          {(target.references ?? []).map((childEdge) => (
            <ReferenceNode
              key={`${childEdge.fieldName}:${childEdge.target.collection}:${childEdge.target.pathId}`}
              edge={childEdge}
              depth={depth + 1}
              instanceLookup={instanceLookup}
              allReferencedBy={allReferencedBy}
              onNavigate={onNavigate}
            />
          ))}
        </div>
      )}
    </div>
  );
}

// --- Member row ---

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

// --- Helpers for value rendering ---

function formatValue(value: InspectedFieldNode | null): string {
  if (!value) return "…";
  if (value.null) return "null";
  // eslint-disable-next-line @typescript-eslint/no-base-to-string -- String() handles unknown at runtime, and the guard above excludes null/undefined.
  if (value.value !== undefined && value.value !== null) return String(value.value);
  if (value.kind === "reference" && value.reference) {
    return value.reference.name ?? `[pathId=${value.reference.pathId}]`;
  }
  if (value.kind === "assetReference") {
    // The inspector emits "reference" for vanilla asset-typed fields today,
    // so this branch covers the modder-edited pathway where a patch has
    // been merged into the inspected view as an AssetReference value.
    const name = (value as { asset?: { name?: string } | null }).asset?.name;
    return name ? `→ ${name}` : "→ ?";
  }
  if (value.kind === "string" && value.value !== null) return JSON.stringify(String(value.value));
  if (value.kind === "array") {
    const count = value.count ?? value.elements?.length ?? 0;
    if (count === 0) return "[]";
    // Check if elements are all simple scalars and short
    const allSimple =
      value.elements?.every((e) => e.value !== undefined || e.kind === "string" || e.null) ?? false;
    if (allSimple && value.elements && value.elements.length <= 4 && value.elements.length > 0) {
      return "[" + value.elements.map((e) => formatValue(e)).join(", ") + "]";
    }
    return `[${count} items]`;
  }
  if (value.kind === "object") {
    const fieldCount = value.fields?.length ?? 0;
    return `{ ${fieldCount} field${fieldCount !== 1 ? "s" : ""} }`;
  }
  return "";
}

interface MemberRowProps {
  member: TemplateMember;
  valueNode: InspectedFieldNode | null;
  parentTypeName: string;
  fieldPath: string;
  instanceLookup: ReadonlyMap<string, TemplateInstanceEntry>;
  onNavigate: (key: string) => void;
}

function MemberRow({
  member,
  valueNode,
  parentTypeName,
  fieldPath,
  instanceLookup,
  onNavigate,
}: MemberRowProps) {
  const [expanded, setExpanded] = useState(false);
  const [dragging, setDragging] = useState(false);

  const isExpandable =
    member.isScalar !== true ||
    member.isCollection === true ||
    member.isTemplateReference === true ||
    valueNode?.kind === "object" ||
    valueNode?.kind === "array" ||
    valueNode?.kind === "matrix";

  const handleToggle = useCallback(() => setExpanded((prev) => !prev), []);

  const tags: string[] = [];
  if (!member.isWritable) tags.push("read-only");
  if (member.isLikelyOdinOnly) tags.push("odin");
  if (member.isCollection) tags.push("collection");
  if (member.isTemplateReference) tags.push("ref");

  const isDraggable = member.isWritable || member.isCollection === true;
  const isNamedArray = member.namedArrayEnumTypeName !== undefined && valueNode?.kind === "array";
  // Both label maps come from the schema query's inlined enumMembers — the
  // visual editor used to fetch them via templatesEnumMembers.
  const namedArrayLabelMap = isNamedArray ? buildNamedArrayLabelMap(member.enumMembers) : null;
  // For enum-leaf scalars (member.enumTypeName set, value is the numeric
  // index), provide the value→name lookup so renderValueLine can surface the
  // enum member name instead of the bare integer.
  const enumLabelMap = member.enumTypeName ? buildNamedArrayLabelMap(member.enumMembers) : null;

  return (
    <div className={styles.memberBlock}>
      {/* --- Header row --- */}
      <div
        className={`${styles.memberHeader} ${isExpandable ? styles.memberHeaderExpandable : ""} ${dragging ? styles.rowDragging : ""}`}
        {...(isExpandable && {
          role: "button",
          tabIndex: 0,
          "aria-expanded": expanded,
          onClick: handleToggle,
          onKeyDown: onKeyActivate(handleToggle),
        })}
        {...(isDraggable && {
          draggable: true,
          onDragStart: (e: React.DragEvent) => {
            e.dataTransfer.effectAllowed = "copy";
            const payload: Record<string, unknown> = {
              templateType: parentTypeName,
              fieldPath,
              typeName: member.typeName,
            };
            if (member.patchScalarKind !== undefined)
              payload.patchScalarKind = member.patchScalarKind;
            if (member.elementTypeName !== undefined)
              payload.elementTypeName = member.elementTypeName;
            if (member.enumTypeName !== undefined) payload.enumTypeName = member.enumTypeName;
            if (member.referenceTypeName !== undefined)
              payload.referenceTypeName = member.referenceTypeName;
            if (member.isCollection !== undefined) payload.isCollection = member.isCollection;
            if (member.isScalar !== undefined) payload.isScalar = member.isScalar;
            if (member.isTemplateReference !== undefined)
              payload.isTemplateReference = member.isTemplateReference;
            if (member.namedArrayEnumTypeName !== undefined)
              payload.namedArrayEnumTypeName = member.namedArrayEnumTypeName;
            e.dataTransfer.setData(
              "text/plain",
              encodeCrossMemberPayload(payload as Parameters<typeof encodeCrossMemberPayload>[0]),
            );
            e.dataTransfer.setData(TEMPLATE_DRAG_TAG, "1");
            e.dataTransfer.setData(MEMBER_DRAG_TAG, "1");
            attachDragChip(e, fieldPath);
            beginTemplateDrag({ kind: "member", templateType: parentTypeName, fieldPath });
            setDragging(true);
          },
          onDragEnd: () => {
            setDragging(false);
            endTemplateDrag();
          },
        })}
      >
        {isDraggable && (
          <span className={styles.rowDragGrip} aria-hidden>
            <GripVertical size={10} />
          </span>
        )}
        {isExpandable ? (
          <button
            type="button"
            className={`${styles.memberExpander} ${expanded ? styles.memberExpanderOpen : ""}`}
            aria-label={expanded ? "Collapse" : "Expand"}
          >
            ▸
          </button>
        ) : null}
        <span className={styles.memberName}>{member.name}</span>
        <span className={styles.memberType}>{member.typeName}</span>
        {tags.length > 0 && (
          <span className={styles.memberTags}>
            {tags.map((t) => (
              <span key={t} className={styles.memberTag}>
                {t}
              </span>
            ))}
          </span>
        )}
      </div>

      {/* --- Value row(s) --- */}
      {valueNode && !expanded && (
        <div className={styles.memberValue}>
          {renderValueLine(valueNode, member, namedArrayLabelMap, enumLabelMap)}
        </div>
      )}

      {/* --- Expanded detail --- */}
      {expanded && (
        <div className={styles.memberDetail}>
          {/* Named array: show labelled rows with collapsible nested fields */}
          {isNamedArray && valueNode.elements && valueNode.elements.length > 0 && (
            <div className={styles.memberNestedList}>
              {valueNode.elements.map((elem, idx) => {
                const label = namedArrayLabelMap?.[idx] ?? `[${idx}]`;
                const elemType = member.elementTypeName ?? "byte";
                const elemSubFields: readonly InspectedFieldNode[] | null =
                  elem.kind === "object" && elem.fields && elem.fields.length > 0
                    ? elem.fields
                    : null;
                return (
                  <ExpandableElementRow
                    // eslint-disable-next-line @eslint-react/no-array-index-key -- idx is the named-array attribute index, not iteration order; stable across renders.
                    key={idx}
                    elem={elem}
                    label={label}
                    typeName={elemType}
                    subFields={elemSubFields}
                    instanceLookup={instanceLookup}
                    onNavigate={onNavigate}
                  />
                );
              })}
            </div>
          )}

          {/* Referenced template: show link */}
          {valueNode?.kind === "reference" && valueNode.reference && (
            <div className={styles.memberFieldInfo}>
              <span className={styles.fieldInfoItem}>
                Target:{" "}
                <strong>
                  {renderReferenceLink(valueNode.reference, instanceLookup, onNavigate)}
                </strong>
              </span>
            </div>
          )}

          {/* Object: show nested fields with values */}
          {valueNode?.kind === "object" && valueNode.fields && valueNode.fields.length > 0 && (
            <div className={styles.memberNestedList}>
              {valueNode.fields.map((subVal, i) => (
                <div key={subVal.name ?? `unnamed-${i}`} className={styles.memberNestedRow}>
                  <span className={styles.chevronSpacer} />
                  <span className={styles.memberNestedLabel}>{subVal.name ?? "<unnamed>"}</span>
                  <span className={styles.memberNestedType}>{subVal.fieldTypeName ?? ""}</span>
                  <span className={styles.memberNestedValue}>{formatValue(subVal)}</span>
                </div>
              ))}
            </div>
          )}

          {/* Array of complex elements: show expandable rows */}
          {valueNode?.kind === "array" &&
            !isNamedArray &&
            valueNode.elements &&
            valueNode.elements.length > 0 && (
              <div className={styles.memberNestedList}>
                {valueNode.elements.map((elem, idx) => {
                  const arrSubFields: readonly InspectedFieldNode[] | null =
                    elem.kind === "object" && elem.fields && elem.fields.length > 0
                      ? elem.fields
                      : null;
                  return (
                    <ExpandableElementRow
                      // eslint-disable-next-line @eslint-react/no-array-index-key -- idx is the array's semantic position in a fixed-shape inspection result, not iteration order.
                      key={idx}
                      elem={elem}
                      label={`[${idx}]`}
                      typeName={elem.fieldTypeName ?? ""}
                      subFields={arrSubFields}
                      instanceLookup={instanceLookup}
                      onNavigate={onNavigate}
                    />
                  );
                })}
                {valueNode.truncated && (
                  <div className={styles.memberNestedTruncated}>
                    <span>… truncated (total {valueNode.count ?? "?"})</span>
                  </div>
                )}
              </div>
            )}

          {/* Multi-dim matrix: render as a 2D grid (3D+ falls back to flat). */}
          {valueNode?.kind === "matrix" &&
            valueNode.dimensions?.length === 2 &&
            valueNode.elements && (
              <MatrixGrid
                rows={valueNode.dimensions[0] ?? 0}
                cols={valueNode.dimensions[1] ?? 0}
                cells={valueNode.elements}
              />
            )}

          {/* Scalar value with depth, when already at leaf */}
          {valueNode && valueNodeKindIsScalar(valueNode) && (
            <div className={styles.memberFieldInfo}>
              <span className={styles.fieldInfoItem}>
                Value: <strong>{formatValue(valueNode)}</strong>
              </span>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

/**
 * Read-only 2D grid renderer for `kind: "matrix"` nodes from the Odin
 * decoder (e.g. AOETiles' `bool[9,9]`). Cells are flat row-major in
 * `cells`; we reshape to rows×cols at render time. The visual editor's
 * write path uses the same shape via cell-addressed Set directives.
 */
function MatrixGrid({
  rows,
  cols,
  cells,
}: {
  rows: number;
  cols: number;
  cells: readonly InspectedFieldNode[];
}) {
  if (rows <= 0 || cols <= 0 || cells.length !== rows * cols) {
    return (
      <div className={styles.memberFieldInfo}>
        <span className={styles.fieldInfoItem}>
          (matrix shape mismatch: {rows}×{cols} declared, {cells.length} cells)
        </span>
      </div>
    );
  }
  return (
    <div className={styles.matrixGrid}>
      {Array.from({ length: rows }, (_, r) => (
        <div key={r} className={styles.matrixRow}>
          {Array.from({ length: cols }, (_, c) => {
            const cell = cells[r * cols + c];
            return (
              <span
                key={c}
                className={`${styles.matrixCell} ${matrixCellTone(cell)}`}
                title={`[${r},${c}] ${formatMatrixCell(cell)}`}
              >
                {formatMatrixCell(cell)}
              </span>
            );
          })}
        </div>
      ))}
    </div>
  );
}

function matrixCellTone(cell: InspectedFieldNode | undefined): string {
  if (!cell) return "";
  if (cell.kind === "bool")
    return cell.value === true ? styles.matrixCellTrue : styles.matrixCellFalse;
  return "";
}

function formatMatrixCell(cell: InspectedFieldNode | undefined): string {
  if (!cell) return "·";
  if (cell.kind === "bool") return cell.value === true ? "■" : "·";
  if (cell.value !== undefined && cell.value !== null) {
    // eslint-disable-next-line @typescript-eslint/no-base-to-string -- numeric or enum scalar
    return String(cell.value);
  }
  return "·";
}

// --- Value rendering helpers ---

function renderValueLine(
  value: InspectedFieldNode,
  member: TemplateMember,
  namedArrayLabelMap: Record<number, string> | null,
  enumLabelMap: Record<number, string> | null,
): React.ReactNode {
  if (value.null) {
    return <span className={styles.valueNull}>null</span>;
  }

  // Named array: show summarised preview in collapsed state.
  if (
    member.namedArrayEnumTypeName &&
    value.kind === "array" &&
    value.elements &&
    value.elements.length > 0
  ) {
    const preview = value.elements.slice(0, 3);
    const rest = value.elements.length - preview.length;
    return (
      <span className={styles.valueArray}>
        {preview.map((elem, idx) => {
          const label = namedArrayLabelMap?.[idx] ?? `[${idx}]`;
          // eslint-disable-next-line @typescript-eslint/no-base-to-string, @typescript-eslint/restrict-template-expressions -- guarded field preview.
          return `${label}=${elem.value ?? "?"}${idx < preview.length - 1 ? ", " : ""}`;
        })}
        {rest > 0 && ` (+${rest} more)`}
      </span>
    );
  }

  if (value.kind === "array") {
    const count = value.count ?? value.elements?.length ?? 0;
    if (count === 0) return <span className={styles.valueEmpty}>[ ]</span>;
    return <span className={styles.valueArray}>{formatValue(value)}</span>;
  }

  if (value.kind === "object") {
    return <span className={styles.valueObject}>{formatValue(value)}</span>;
  }

  if (value.kind === "reference") {
    return <span className={styles.valueRef}>{formatValue(value)}</span>;
  }

  if (value.kind === "string" || typeof value.value === "string") {
    return <span className={styles.valueString}>{formatValue(value)}</span>;
  }

  if (value.value !== undefined && value.value !== null) {
    // Enum-leaf field: the inspector returns the numeric index; surface the
    // member name when we can resolve it, with the raw value alongside as a
    // disambiguator for the modder. Falls through to the plain scalar render
    // when the enum member set hasn't loaded yet or the value isn't defined.
    const enumLabel = resolveEnumLeafLabel(value.value, enumLabelMap);
    if (enumLabel !== null) {
      return (
        <span className={styles.valueScalar}>
          {enumLabel}
          {/* eslint-disable-next-line @typescript-eslint/no-base-to-string -- numeric scalar */}
          <span className={styles.valueEnumIndex}> ({String(value.value)})</span>
        </span>
      );
    }
    // eslint-disable-next-line @typescript-eslint/no-base-to-string -- String() handles unknown at runtime.
    return <span className={styles.valueScalar}>{String(value.value)}</span>;
  }

  return <span className={styles.valueUnknown}>?</span>;
}

function renderReferenceLink(
  reference: InspectedReference,
  instanceLookup: ReadonlyMap<string, TemplateInstanceEntry>,
  onNavigate: (key: string) => void,
): React.ReactNode {
  if (!reference.pathId) return <span>{reference.name ?? "null"}</span>;
  // Find the referenced template in the index by matching name + className,
  // since the reference's fileId is a dependency index, not a collection name.
  let targetKey: string | null = null;
  for (const [k, inst] of instanceLookup) {
    if (
      inst.identity.pathId === reference.pathId &&
      (reference.name === undefined || inst.name === reference.name)
    ) {
      targetKey = k;
      break;
    }
  }
  const target = targetKey ? instanceLookup.get(targetKey) : undefined;
  const label = target
    ? `${target.className}:${target.name}`
    : (reference.name ?? reference.className ?? `[pathId=${reference.pathId}]`);
  return (
    <button
      type="button"
      className={styles.refLink}
      onClick={(e) => {
        e.stopPropagation();
        if (targetKey) onNavigate(targetKey);
      }}
    >
      {label}
    </button>
  );
}

function valueNodeKindIsScalar(node: InspectedFieldNode): boolean {
  return node.kind !== "array" && node.kind !== "object" && node.kind !== "reference";
}

function buildNamedArrayLabelMap(
  members: readonly { readonly name: string; readonly value: number }[] | null | undefined,
): Record<number, string> | null {
  if (!members) return null;
  const map: Record<number, string> = {};
  for (const m of members) {
    map[m.value] = m.name;
  }
  return map;
}

// --- Expandable element row (used by both named and regular arrays) ---

function ExpandableElementRow({
  elem,
  label,
  typeName,
  subFields,
  instanceLookup,
  onNavigate,
}: {
  elem: InspectedFieldNode;
  label: string;
  typeName: string;
  subFields: readonly InspectedFieldNode[] | null;
  instanceLookup: ReadonlyMap<string, TemplateInstanceEntry>;
  onNavigate: (key: string) => void;
}) {
  const [subExpanded, setSubExpanded] = useState(false);
  const hasChildren = subFields !== null;

  // When the element resolves to a known template, render the value as a
  // navigable link instead of plain text. Covers reference-array fields like
  // SkillTemplate.EventHandlers (List<SkillEventHandlerTemplate>) and
  // SkillGroup.Skills (List<SkillTemplate>).
  const referenceLink =
    elem.kind === "reference" && elem.reference
      ? renderReferenceLink(elem.reference, instanceLookup, onNavigate)
      : null;

  return (
    <div>
      <div
        className={`${styles.memberNestedRow} ${hasChildren ? styles.memberHeaderExpandable : ""}`}
        {...(hasChildren && {
          role: "button",
          tabIndex: 0,
          "aria-expanded": subExpanded,
          onClick: () => setSubExpanded(!subExpanded),
          onKeyDown: onKeyActivate(() => setSubExpanded(!subExpanded)),
        })}
      >
        {hasChildren ? (
          <button
            type="button"
            className={`${styles.chevron} ${subExpanded ? styles.chevronOpen : ""}`}
            aria-label={subExpanded ? "Collapse" : "Expand"}
            tabIndex={-1}
          >
            ▸
          </button>
        ) : (
          <span className={styles.chevronSpacer} />
        )}
        <span className={styles.memberNestedLabel}>{label}</span>
        <span className={styles.memberNestedType}>{typeName}</span>
        <span className={styles.memberNestedValue}>{referenceLink ?? formatValue(elem)}</span>
      </div>
      {subExpanded && subFields && (
        <div className={styles.memberNestedSubList}>
          {subFields.map((f, i) => (
            <NestedValueRow key={f.name ?? `unnamed-${i}`} value={f} />
          ))}
        </div>
      )}
    </div>
  );
}

// --- Recursive nested value row ---

function NestedValueRow({ value }: { value: InspectedFieldNode }) {
  const [open, setOpen] = useState(false);
  const children =
    value.kind === "object" && value.fields && value.fields.length > 0 ? value.fields : null;
  const hasChildren = children !== null;

  return (
    <div>
      <div
        className={`${styles.memberNestedSubRow} ${hasChildren ? styles.memberHeaderExpandable : ""}`}
        {...(hasChildren && {
          role: "button",
          tabIndex: 0,
          "aria-expanded": open,
          onClick: () => setOpen(!open),
          onKeyDown: onKeyActivate(() => setOpen(!open)),
        })}
      >
        {hasChildren ? (
          <button
            type="button"
            className={`${styles.chevron} ${open ? styles.chevronOpen : ""}`}
            aria-label={open ? "Collapse" : "Expand"}
            tabIndex={-1}
          >
            ▸
          </button>
        ) : (
          <span className={styles.chevronSpacer} />
        )}
        <span className={styles.memberNestedLabel}>{value.name ?? "?"}</span>
        <span className={styles.memberNestedType}>{value.fieldTypeName ?? ""}</span>
        <span className={styles.memberNestedValue}>{formatValue(value)}</span>
      </div>
      {open && children && (
        <div className={styles.memberNestedSubList}>
          {children.map((c, i) => (
            <NestedValueRow key={c.name ?? `unnamed-${i}`} value={c} />
          ))}
        </div>
      )}
    </div>
  );
}

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
  useEffect(() => {
    if (!open) return;
    const handler = (e: MouseEvent) => {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) {
        setOpen(false);
      }
    };
    document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, [open]);

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
