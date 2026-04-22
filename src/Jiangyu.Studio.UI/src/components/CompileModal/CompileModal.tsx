import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { FolderOpen, Play, X } from "lucide-react";
import { Modal } from "../Modal/Modal.tsx";
import { rpcCall } from "../../lib/rpc.ts";
import {
  useCompileSummary,
  formatDurationShort,
  type CompileLogEntry,
  type CompileState,
  type CompileSummary,
} from "../../lib/compile.ts";
import styles from "./CompileModal.module.css";

interface CompileModalProps {
  readonly state: CompileState;
  readonly onClose: () => void;
  readonly onStart: () => void;
}

export function CompileModal({ state, onClose, onStart }: CompileModalProps) {
  const summary = useCompileSummary(true);
  return (
    <Modal onClose={onClose} ariaLabelledBy="compile-title">
      <div className={styles.dialog}>
        <Header onClose={onClose} />
        <div className={styles.body}>
          <LogPanel state={state} />
          <InfoPanel state={state} summary={summary} onStart={onStart} onClose={onClose} />
        </div>
      </div>
    </Modal>
  );
}

function Header({ onClose }: { onClose: () => void }) {
  return (
    <div className={styles.header}>
      <span id="compile-title">Compile · 编译</span>
      <button type="button" className={styles.close} aria-label="Close" onClick={onClose}>
        <X size={14} />
      </button>
    </div>
  );
}

// --- Log ---------------------------------------------------------------------

function LogPanel({ state }: { state: CompileState }) {
  return (
    <div className={styles.logShell}>
      <span className={styles.logEyebrow}>Output · 输出</span>
      <LogScroller state={state} />
    </div>
  );
}

function LogScroller({ state }: { state: CompileState }) {
  const scrollRef = useRef<HTMLDivElement>(null);
  const stickRef = useRef(true);

  const onScroll = () => {
    const el = scrollRef.current;
    if (el === null) return;
    stickRef.current = el.scrollHeight - el.scrollTop - el.clientHeight < 12;
  };

  useLayoutEffect(() => {
    if (!stickRef.current) return;
    const el = scrollRef.current;
    if (el !== null) el.scrollTop = el.scrollHeight;
  }, [state.logs]);

  if (state.status === "idle" && state.logs.length === 0) {
    return (
      <div className={styles.log}>
        <div className={styles.logIdle}>
          <span className={styles.logPromptPrefix}>$</span>
          <span className={styles.logPromptCaret}>_</span>
        </div>
      </div>
    );
  }

  return (
    <div className={styles.log} ref={scrollRef} onScroll={onScroll}>
      {state.status === "running" && state.phase !== null && (
        <LogLine level="info" prefix="→" message={state.phase} />
      )}
      {state.logs.map((entry) => (
        <LogLine
          key={entry.id}
          level={entry.level}
          prefix={glyphFor(entry.level)}
          message={entry.message}
        />
      ))}
      {state.status === "running" && (
        <div className={`${styles.logLine} ${styles.logLine_running}`}>
          <span className={styles.logPromptPrefix}>→</span>
          <span className={styles.logMessage}>
            {state.statusLine ?? "working…"}
            <span className={styles.logPromptCaret}>_</span>
          </span>
        </div>
      )}
    </div>
  );
}

function LogLine({
  level,
  prefix,
  message,
}: {
  level: CompileLogEntry["level"];
  prefix: string;
  message: string;
}) {
  return (
    <div className={`${styles.logLine} ${styles[`logLine_${level}`]}`}>
      <span className={styles.logPrefix}>{prefix}</span>
      <span className={styles.logMessage}>{message}</span>
    </div>
  );
}

function glyphFor(level: CompileLogEntry["level"]): string {
  if (level === "warn") return "△";
  if (level === "error") return "✕";
  return "→";
}

// --- Info panel --------------------------------------------------------------

function InfoPanel({
  state,
  summary,
  onStart,
  onClose,
}: {
  state: CompileState;
  summary: CompileSummary | null;
  onStart: () => void;
  onClose: () => void;
}) {
  // While running, tick every 500ms so the Duration stat advances in real time.
  // Stops as soon as the compile finishes — after that, duration is frozen at
  // finishedAt − startedAt.
  useTickerWhileRunning(state.status === "running");

  const warningCount = useMemo(
    () => state.logs.filter((e) => e.level === "warn").length,
    [state.logs],
  );
  const errorCount = useMemo(
    () => state.logs.filter((e) => e.level === "error").length,
    [state.logs],
  );

  const totalReplacements = summary
    ? summary.modelReplacements +
      summary.textureReplacements +
      summary.spriteReplacements +
      summary.audioReplacements
    : 0;

  const isRunning = state.status === "running";
  const isDone = state.status === "success" || state.status === "failed";

  return (
    <div className={styles.info}>
      <Section title="Mod · 模块">
        {summary === null ? (
          <p className={styles.placeholder}>Loading…</p>
        ) : summary.modName === null ? (
          <p className={styles.placeholder}>No manifest at jiangyu.json</p>
        ) : (
          <>
            <div className={styles.modName}>{summary.modName}</div>
            <div className={styles.modMeta}>
              <span>v{summary.modVersion ?? "0.0.0"}</span>
              {summary.modAuthor !== null && <span> · {summary.modAuthor}</span>}
            </div>
          </>
        )}
      </Section>

      <Section title="Assets · 资产">
        <div className={styles.statGrid}>
          <Stat label="Models" value={summary?.modelReplacements ?? null} />
          <Stat label="Textures" value={summary?.textureReplacements ?? null} />
          <Stat label="Sprites" value={summary?.spriteReplacements ?? null} />
          <Stat label="Audio" value={summary?.audioReplacements ?? null} />
        </div>
        <div className={styles.subStats}>
          <SubStat
            label="Replacements"
            value={summary !== null ? String(totalReplacements) : "—"}
          />
          <SubStat
            label="Additions"
            value={summary !== null ? String(summary.additionFiles) : "—"}
          />
        </div>
      </Section>

      <Section title="Templates · 模板">
        <div className={styles.subStats}>
          <SubStat
            label="KDL files"
            value={summary !== null ? String(summary.templateFiles) : "—"}
          />
        </div>
      </Section>

      {(isRunning || isDone) && (
        <Section title="Result · 结果" tone={state.status === "failed" ? "failed" : "default"}>
          <div className={styles.statGrid}>
            {warningCount > 0 ? (
              <Stat label="Warnings" value={warningCount} tone="warn" />
            ) : (
              <Stat label="Warnings" value={warningCount} />
            )}
            <Stat label="Duration" value={formatDurationStat(state)} mono />
          </div>
          {state.status === "failed" && state.errorMessage !== null && (
            <p className={styles.resultError}>{state.errorMessage}</p>
          )}
          {state.status === "success" && state.bundlePath !== null && (
            <p className={styles.resultPath} title={state.bundlePath}>
              {state.bundlePath}
            </p>
          )}
          {errorCount > 0 && state.status === "failed" && (
            <p className={styles.resultMeta}>
              {errorCount} error{errorCount === 1 ? "" : "s"}
            </p>
          )}
        </Section>
      )}

      <div className={styles.actions}>
        <button
          type="button"
          className={styles.primaryButton}
          onClick={onStart}
          disabled={isRunning}
        >
          {isRunning ? (
            <>Compiling…</>
          ) : isDone ? (
            <>
              <Play size={12} /> Compile again
            </>
          ) : (
            <>
              <Play size={12} /> Run compile
            </>
          )}
        </button>
        {state.status === "success" && state.bundlePath !== null && (
          <button type="button" className={styles.ghostButton} onClick={() => revealBundle(state)}>
            <FolderOpen size={12} /> Reveal bundle
          </button>
        )}
        <button type="button" className={styles.ghostButton} onClick={onClose}>
          {isRunning ? "Hide" : "Close"}
        </button>
      </div>
    </div>
  );
}

function revealBundle(state: CompileState) {
  if (state.bundlePath === null) return;
  rpcCall<null>("revealInExplorer", { path: state.bundlePath }).catch((err: unknown) => {
    console.error("[Compile] reveal failed:", err);
  });
}

function Section({
  title,
  tone,
  children,
}: {
  title: string;
  tone?: "default" | "failed";
  children: React.ReactNode;
}) {
  return (
    <section className={`${styles.section} ${tone === "failed" ? styles.sectionFailed : ""}`}>
      <h3 className={styles.sectionTitle}>{title}</h3>
      <div className={styles.sectionBody}>{children}</div>
    </section>
  );
}

function Stat({
  label,
  value,
  mono,
  tone,
}: {
  label: string;
  value: number | string | null;
  mono?: boolean;
  tone?: "warn";
}) {
  return (
    <div className={styles.stat}>
      <div className={styles.statLabel}>{label}</div>
      <div
        className={`${styles.statValue} ${mono === true ? styles.statValueMono : ""} ${tone === "warn" ? styles.statValueWarn : ""}`}
      >
        {value === null ? "—" : value}
      </div>
    </div>
  );
}

function SubStat({ label, value }: { label: string; value: string }) {
  return (
    <div className={styles.subStat}>
      <span className={styles.subStatLabel}>{label}</span>
      <span className={styles.subStatValue}>{value}</span>
    </div>
  );
}

function useTickerWhileRunning(running: boolean): void {
  const [, setTick] = useState(0);
  useEffect(() => {
    if (!running) return;
    const handle = setInterval(() => setTick((n) => n + 1), 500);
    return () => clearInterval(handle);
  }, [running]);
}

function formatDurationStat(state: CompileState): string {
  if (state.startedAt === null) return "—";
  const end = state.finishedAt ?? Date.now();
  return formatDurationShort(end - state.startedAt);
}
