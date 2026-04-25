import uFuzzy from "@leeoniya/ufuzzy";
import { useVirtualizer } from "@tanstack/react-virtual";
import { GripVertical } from "lucide-react";
import {
  Fragment,
  useCallback,
  useDeferredValue,
  useEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import { rpcCall } from "@lib/rpc.ts";
import { attachDragChip } from "@lib/drag/chip.ts";
import { generatePatchKdl, generateCloneKdl } from "@lib/kdlSnippets.ts";
import {
  encodeCrossInstancePayload,
  TEMPLATE_DRAG_TAG,
  INSTANCE_DRAG_TAG,
  MEMBER_DRAG_TAG,
  beginTemplateDrag,
  endTemplateDrag,
} from "@lib/drag/crossInstance.ts";
import { encodeCrossMemberPayload } from "@lib/drag/crossMember.ts";
import { useToast } from "@lib/toast/toast.tsx";
import {
  DEFAULT_TEMPLATE_BROWSER_STATE,
  type TemplateBrowserState,
} from "@lib/panes/browserState.ts";
import { useDebouncedScrollTop } from "@lib/ui/useDebouncedScrollTop.ts";
import { onKeyActivate } from "@lib/ui/a11y.ts";
import { Spinner } from "@components/Spinner/Spinner.tsx";
import {
  TemplateFilePicker,
  type PickerResult,
} from "@components/TemplateFilePicker/TemplateFilePicker.tsx";
import {
  DetailTitle,
  MetaBlock,
  MetaRow,
  SectionHeader,
} from "@components/DetailPanel/DetailPanel.tsx";
import styles from "./TemplateBrowser.module.css";

// --- RPC types ---

interface TemplateIndexStatus {
  readonly state: "current" | "stale" | "missing" | "noGame";
  readonly reason?: string;
  readonly instanceCount?: number;
  readonly typeCount?: number;
}

interface TemplateTypeEntry {
  readonly className: string;
  readonly count: number;
}

interface TemplateInstanceEntry {
  readonly name: string;
  readonly className: string;
  readonly identity: { readonly collection: string; readonly pathId: number };
  readonly references?: readonly TemplateEdge[];
}

interface TemplateEdge {
  readonly fieldName: string;
  readonly target: { readonly collection: string; readonly pathId: number };
}

interface TemplateReferenceEntry {
  readonly source: { readonly collection: string; readonly pathId: number };
  readonly fieldName: string;
}

interface TemplateSearchResult {
  readonly types: readonly TemplateTypeEntry[];
  readonly instances: readonly TemplateInstanceEntry[];
  readonly referencedBy?: Readonly<Record<string, readonly TemplateReferenceEntry[]>>;
}

interface TemplateMember {
  readonly name: string;
  readonly typeName: string;
  readonly typeFullName?: string;
  readonly isWritable: boolean;
  readonly isInherited: boolean;
  readonly isLikelyOdinOnly?: boolean;
  readonly isCollection?: boolean;
  readonly isScalar?: boolean;
  readonly isTemplateReference?: boolean;
  readonly patchScalarKind?: string;
  readonly elementTypeName?: string;
  readonly enumTypeName?: string;
  readonly referenceTypeName?: string;
  readonly namedArrayEnumTypeName?: string;
}

interface TemplateQueryResult {
  readonly kind: "typenode" | "leaf";
  readonly resolvedPath?: string;
  readonly typeName?: string;
  readonly typeFullName?: string;
  readonly isWritable: boolean;
  readonly patchScalarKind?: string;
  readonly enumMemberNames?: readonly string[];
  readonly referenceTargetTypeName?: string;
  readonly isLikelyOdinOnly?: boolean;
  readonly members?: readonly TemplateMember[];
}

// --- RPC calls ---

function templatesIndexStatus(): Promise<TemplateIndexStatus> {
  return rpcCall<TemplateIndexStatus>("templatesIndexStatus");
}

function templatesIndex(): Promise<TemplateIndexStatus> {
  return rpcCall<TemplateIndexStatus>("templatesIndex");
}

function templatesSearch(): Promise<TemplateSearchResult> {
  return rpcCall<TemplateSearchResult>("templatesSearch");
}

function templatesQuery(typeName: string, fieldPath?: string): Promise<TemplateQueryResult> {
  return rpcCall<TemplateQueryResult>("templatesQuery", { typeName, fieldPath });
}

// --- Helpers ---

const uf = new uFuzzy({});
const ROW_HEIGHT = 28;

const instanceKey = (inst: TemplateInstanceEntry): string =>
  `${inst.identity.collection}:${inst.identity.pathId}`;

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
      setNavHistory((prev) => [...prev.slice(0, navIndex + 1), key]);
      setNavIndex((prev) => prev + 1);
    },
    [navIndex],
  );

  const goBack = useCallback(() => {
    if (!canGoBack) return;
    const newIdx = navIndex - 1;
    const target = navHistory[newIdx];
    if (target === undefined) return;
    setNavIndex(newIdx);
    setFocusedKey(target);
  }, [canGoBack, navIndex, navHistory]);

  const goForward = useCallback(() => {
    if (!canGoForward) return;
    const newIdx = navIndex + 1;
    const target = navHistory[newIdx];
    if (target === undefined) return;
    setNavIndex(newIdx);
    setFocusedKey(target);
  }, [canGoForward, navIndex, navHistory]);

  const [memberData, setMemberData] = useState<TemplateQueryResult | null>(null);
  const [membersLoading, setMembersLoading] = useState(false);
  const memberTokenRef = useRef(0);

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

  // --- Load full index ---
  useEffect(() => {
    if (status?.state !== "current") {
      setAllTypes([]);
      setAllInstances([]);
      setReferencedBy({});
      return;
    }
    let cancelled = false;
    setLoadingData(true);
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

  useEffect(() => {
    const token = ++memberTokenRef.current;
    setMemberData(null);

    if (!focusedInstance) {
      setMembersLoading(false);
      return;
    }

    setMembersLoading(true);
    void templatesQuery(focusedInstance.className)
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

  // --- Virtualiser ---
  const listRef = useRef<HTMLDivElement>(null);
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
        <div className={styles.statusBanner}>
          <Spinner size={14} />
          <span>Checking template index…</span>
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
                ? "No template index yet"
                : "Template index is out of date"}
          </p>
          {status.reason && <p className={styles.gateReason}>{status.reason}</p>}
          {indexError !== null && <p className={styles.gateReason}>{indexError}</p>}
          {!noGame && (
            <button
              type="button"
              className={styles.gateButton}
              disabled={indexing}
              onClick={() => void handleBuildIndex()}
            >
              {indexing && (
                <Spinner
                  size={12}
                  trackColor="var(--track-on-accent)"
                  accentColor="var(--paper-0)"
                />
              )}
              {indexing ? "Indexing…" : stale ? "Re-index" : "Index templates"}
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
        <div className={styles.statusBanner}>
          <Spinner size={14} />
          <span>Loading templates…</span>
        </div>
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
                  const select = () => {
                    setFocusedKey(key);
                    pushNav(key);
                  };
                  return (
                    <InstanceRow
                      key={key}
                      inst={inst}
                      ownerInst={ownerInst}
                      focused={focused}
                      virtualSize={vRow.size}
                      virtualStart={vRow.start}
                      onSelect={select}
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
              <div className={styles.scaffoldMenuDropdown}>
                <button
                  type="button"
                  className={styles.scaffoldMenuItem}
                  onClick={() => {
                    setMenuOpen(false);
                    onCreatePatch();
                  }}
                >
                  Create Patch
                </button>
                <button
                  type="button"
                  className={styles.scaffoldMenuItem}
                  onClick={() => {
                    setMenuOpen(false);
                    onCreateClone();
                  }}
                >
                  Create Clone
                </button>
                <div className={styles.scaffoldMenuSep} />
                <button
                  type="button"
                  className={styles.scaffoldMenuItem}
                  onClick={() => {
                    setMenuOpen(false);
                    onPatchToFile();
                  }}
                >
                  Add patch to file…
                </button>
                <button
                  type="button"
                  className={styles.scaffoldMenuItem}
                  onClick={() => {
                    setMenuOpen(false);
                    onCloneToFile();
                  }}
                >
                  Add clone to file…
                </button>
              </div>
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
                const src = instanceLookup.get(`${r.source.collection}:${r.source.pathId}`);
                const key = `${r.source.collection}:${r.source.pathId}`;
                const isLast = i === refs.length - 1;
                return (
                  <Fragment key={i}>
                    <span className={styles.refEntry}>
                      <button
                        type="button"
                        className={styles.refLink}
                        onClick={() => onNavigate(key)}
                      >
                        {src ? `${src.name} (${src.className})` : key}
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
            {instance.references.map((edge, i) => (
              <ReferenceNode
                key={`${edge.target.collection}:${edge.target.pathId}:${i}`}
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

      {(membersLoading || (memberData?.members && memberData.members.length > 0)) && (
        <div className={styles.memberSection}>
          <SectionHeader>Fields</SectionHeader>
          {membersLoading ? (
            <div className={styles.memberLoading}>
              <Spinner size={12} />
              <span>Loading fields…</span>
            </div>
          ) : (
            <div className={styles.memberList}>
              {memberData?.members?.map((m) => (
                <MemberRow
                  key={m.name}
                  member={m}
                  depth={0}
                  parentTypeName={instance.className}
                  fieldPath={m.name}
                />
              ))}
            </div>
          )}
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
          {target.references.map((childEdge, i) => (
            <ReferenceNode
              key={`${childEdge.target.collection}:${childEdge.target.pathId}:${i}`}
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
  inst: TemplateInstanceEntry;
  ownerInst: TemplateInstanceEntry | undefined;
  focused: boolean;
  virtualSize: number;
  virtualStart: number;
  onSelect: () => void;
}

/// Instance row in the list panel — virtualised, draggable into the visual
/// editor. Pulled into its own component so each row can hold its own drag
/// state and render the hover-revealed grip glyph without lifting that state
/// to the (already-busy) parent component.
function InstanceRow({
  inst,
  ownerInst,
  focused,
  virtualSize,
  virtualStart,
  onSelect,
}: InstanceRowProps) {
  const [dragging, setDragging] = useState(false);
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
      onClick={onSelect}
      onKeyDown={onKeyActivate(onSelect)}
    >
      <span className={styles.rowDragGrip} aria-hidden>
        <GripVertical size={10} />
      </span>
      <span className={styles.rowName}>{inst.name}</span>
      {ownerInst && <span className={styles.rowOwner}>← {ownerInst.name}</span>}
    </div>
  );
}

interface MemberRowProps {
  member: TemplateMember;
  depth: number;
  parentTypeName: string;
  fieldPath: string;
}

function MemberRow({ member, depth, parentTypeName, fieldPath }: MemberRowProps) {
  const [expanded, setExpanded] = useState(false);
  const [fieldInfo, setFieldInfo] = useState<TemplateQueryResult | null>(null);
  const [loading, setLoading] = useState(false);
  const [dragging, setDragging] = useState(false);
  const loadedRef = useRef(false);

  const isExpandable =
    member.isScalar !== true || member.isCollection === true || member.isTemplateReference === true;

  const handleToggle = useCallback(() => {
    const next = !expanded;
    setExpanded(next);
    if (next && !loadedRef.current) {
      loadedRef.current = true;
      setLoading(true);
      void templatesQuery(parentTypeName, fieldPath)
        .then((result) => setFieldInfo(result))
        .catch(() => {})
        .finally(() => setLoading(false));
    }
  }, [expanded, parentTypeName, fieldPath]);

  const tags: string[] = [];
  if (!member.isWritable) tags.push("read-only");
  if (member.isLikelyOdinOnly) tags.push("odin");
  if (member.isCollection) tags.push("collection");
  if (member.isTemplateReference) tags.push("ref");

  const isDraggable = member.isWritable || member.isCollection === true;

  return (
    <>
      <div
        className={`${styles.memberRow} ${isExpandable ? styles.memberRowExpandable : ""} ${dragging ? styles.rowDragging : ""}`}
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
            // Carry the full member schema so the drop site can build a
            // correctly-typed directive (TemplateReference vs Enum vs scalar
            // vs NamedArray-element) without re-querying the catalog. Undefined
            // fields are stripped — exactOptionalPropertyTypes treats absent
            // and undefined as distinct, and the payload's keys are readonly.
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
            className={`${styles.refExpander} ${expanded ? styles.refExpanderOpen : ""}`}
            aria-label={expanded ? "Collapse" : "Expand"}
          >
            ▸
          </button>
        ) : (
          <span className={styles.refExpanderSpacer} />
        )}
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
      {expanded && (
        <div className={styles.memberChildren}>
          {loading ? (
            <div className={styles.memberLoading}>
              <Spinner size={10} />
              <span>Loading…</span>
            </div>
          ) : fieldInfo ? (
            <>
              {/* Patch info summary */}
              {fieldInfo.kind === "leaf" && (
                <div className={styles.memberFieldInfo}>
                  {fieldInfo.patchScalarKind && (
                    <span className={styles.fieldInfoItem}>
                      Patch as: <strong>{fieldInfo.patchScalarKind}</strong>
                    </span>
                  )}
                  {fieldInfo.referenceTargetTypeName && (
                    <span className={styles.fieldInfoItem}>
                      Target: <strong>{fieldInfo.referenceTargetTypeName}</strong>
                    </span>
                  )}
                  {fieldInfo.isLikelyOdinOnly && (
                    <span className={styles.fieldInfoItem}>Likely Odin-serialised only</span>
                  )}
                  {fieldInfo.enumMemberNames && fieldInfo.enumMemberNames.length > 0 && (
                    <div className={styles.fieldInfoEnum}>
                      <span className={styles.fieldInfoItem}>Values:</span>
                      <div className={styles.enumValues}>
                        {fieldInfo.enumMemberNames.map((name) => (
                          <span key={name} className={styles.enumValue}>
                            {name}
                          </span>
                        ))}
                      </div>
                    </div>
                  )}
                </div>
              )}
              {/* Sub-members */}
              {fieldInfo.members &&
                fieldInfo.members.length > 0 &&
                fieldInfo.members.map((sub) => (
                  <MemberRow
                    key={sub.name}
                    member={sub}
                    depth={depth + 1}
                    parentTypeName={parentTypeName}
                    fieldPath={`${fieldPath}.${sub.name}`}
                  />
                ))}
              {fieldInfo.kind === "leaf" &&
                !fieldInfo.patchScalarKind &&
                !fieldInfo.referenceTargetTypeName &&
                !fieldInfo.enumMemberNames && (
                  <div className={styles.memberFieldInfo}>
                    <span className={styles.fieldInfoItem}>Not directly patchable</span>
                  </div>
                )}
            </>
          ) : null}
        </div>
      )}
    </>
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
  useEffect(() => {
    setHighlightIdx(0);
  }, [filtered.length]);

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
