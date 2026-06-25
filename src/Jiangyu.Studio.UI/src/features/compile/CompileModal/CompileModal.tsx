import { useEffect, useLayoutEffect, useReducer, useRef, useState } from "react";
import { FolderOpen, Package, Play, Rocket } from "lucide-react";
import { Modal } from "@shared/ui/Modal/Modal";
import { ModalHeader } from "@shared/ui/Modal/ModalHeader";
import { Button } from "@shared/ui/Button/Button";
import { rpcCall } from "@shared/rpc";
import {
  useCompileStore,
  useCompileSummary,
  formatDurationShort,
  type CompileLogEntry,
  type CompileSummary,
} from "@features/compile";
import styles from "./CompileModal.module.css";

interface CompileModalProps {
  readonly onClose: () => void;
  readonly onDeploy: () => void;
  readonly onPackage: () => void;
}

export function CompileModal({ onClose, onDeploy, onPackage }: CompileModalProps) {
  const summary = useCompileSummary(true);
  return (
    <Modal onClose={onClose} ariaLabelledBy="compile-title" width={1100} height={760}>
      <ModalHeader id="compile-title" title="Compile · 编译" onClose={onClose} />
      <div className={styles.body}>
        <LogPanel />
        <InfoPanel summary={summary} onClose={onClose} onDeploy={onDeploy} onPackage={onPackage} />
      </div>
    </Modal>
  );
}

// --- Log ---------------------------------------------------------------------

function LogPanel() {
  return (
    <div className={styles.logShell}>
      <span className={styles.logEyebrow}>Output · 输出</span>
      <LogScroller />
    </div>
  );
}

function LogScroller() {
  const status = useCompileStore((s) => s.status);
  const phase = useCompileStore((s) => s.phase);
  const statusLine = useCompileStore((s) => s.statusLine);
  const logs = useCompileStore((s) => s.logs);
  const droppedLogCount = useCompileStore((s) => s.droppedLogCount);

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
  }, [logs]);

  if (status === "idle" && logs.length === 0) {
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
      {droppedLogCount > 0 && (
        <LogLine
          level="info"
          prefix="…"
          message={`${droppedLogCount} earlier line${droppedLogCount === 1 ? "" : "s"} dropped`}
        />
      )}
      {status === "running" && phase !== null && (
        <LogLine level="info" prefix="→" message={phase} />
      )}
      {logs.map((entry) => (
        <LogLine
          key={entry.id}
          level={entry.level}
          prefix={glyphFor(entry.level)}
          message={entry.message}
        />
      ))}
      {status === "running" && (
        <div className={`${styles.logLine} ${styles.logLine_running}`}>
          <span className={styles.logPromptPrefix}>→</span>
          <span className={styles.logMessage}>
            {statusLine ?? "working…"}
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
  summary,
  onClose,
  onDeploy,
  onPackage,
}: {
  summary: CompileSummary | null;
  onClose: () => void;
  onDeploy: () => void;
  onPackage: () => void;
}) {
  const status = useCompileStore((s) => s.status);
  const warnCount = useCompileStore((s) => s.warnCount);
  const errorCount = useCompileStore((s) => s.errorCount);
  const errorMessage = useCompileStore((s) => s.errorMessage);
  const bundlePath = useCompileStore((s) => s.bundlePath);
  const startedAt = useCompileStore((s) => s.startedAt);
  const finishedAt = useCompileStore((s) => s.finishedAt);
  const start = useCompileStore((s) => s.start);
  const [releaseBuild, setReleaseBuild] = useState(false);

  // While running, tick every 500ms so the Duration stat advances in real time.
  // Stops as soon as the compile finishes — after that, duration is frozen at
  // finishedAt − startedAt.
  useTickerWhileRunning(status === "running");

  const isRunning = status === "running";
  const isDone = status === "success" || status === "failed";

  return (
    <div className={styles.info}>
      <Section title="Mod · 模组">
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
          <Stat label="Models" value={summary?.models ?? null} />
          <Stat label="Textures" value={summary?.textures ?? null} />
          <Stat label="Sprites" value={summary?.sprites ?? null} />
          <Stat label="Audio" value={summary?.audio ?? null} />
        </div>
        <div className={styles.subStats}>
          <SubStat
            label="Replacements"
            value={summary !== null ? String(summary.replacementFiles) : "—"}
          />
          <SubStat
            label="Additions"
            value={summary !== null ? String(summary.additionFiles) : "—"}
          />
        </div>
      </Section>

      <Section title="Templates · 模板">
        <div className={styles.statGrid}>
          <Stat label="Patches" value={summary?.templatePatches ?? null} />
          <Stat label="Clones" value={summary?.templateClones ?? null} />
        </div>
        <div className={styles.subStats}>
          <SubStat
            label="KDL files"
            value={summary !== null ? String(summary.templateFiles) : "—"}
          />
        </div>
      </Section>

      {(isRunning || isDone) && (
        <Section title="Result · 结果" tone={status === "failed" ? "failed" : "default"}>
          <div className={styles.statGrid}>
            {warnCount > 0 ? (
              <Stat label="Warnings" value={warnCount} tone="warn" />
            ) : (
              <Stat label="Warnings" value={warnCount} />
            )}
            <Stat label="Duration" value={formatDurationStat(startedAt, finishedAt)} mono />
          </div>
          {status === "failed" && errorMessage !== null && (
            <p className={styles.resultError}>{errorMessage}</p>
          )}
          {errorCount > 0 && status === "failed" && (
            <p className={styles.resultMeta}>
              {errorCount} error{errorCount === 1 ? "" : "s"}
            </p>
          )}
        </Section>
      )}

      {status === "success" && bundlePath !== null && (
        <p className={styles.resultPath} title={bundlePath}>
          {bundlePath}
        </p>
      )}
      <div className={styles.actions}>
        <label className={styles.releaseToggle}>
          <input
            type="checkbox"
            checked={releaseBuild}
            disabled={isRunning}
            onChange={(e) => setReleaseBuild(e.target.checked)}
          />
          Release build · 发布 (excludes dev-only sources)
        </label>
        <Button
          variant="primary"
          size="md"
          onClick={() => start(releaseBuild)}
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
        </Button>
        {status === "success" && bundlePath !== null && (
          <Button variant="ghost" onClick={() => revealBundle(bundlePath)}>
            <FolderOpen size={12} /> Reveal bundle
          </Button>
        )}
        {status === "success" && (
          <>
            <Button variant="ghost" onClick={onDeploy}>
              <Rocket size={12} /> Deploy
            </Button>
            <Button variant="ghost" onClick={onPackage}>
              <Package size={12} /> Package
            </Button>
          </>
        )}
        <Button variant="ghost" onClick={onClose}>
          {isRunning ? "Hide" : "Close"}
        </Button>
      </div>
    </div>
  );
}

function revealBundle(bundlePath: string) {
  rpcCall<null>("revealInExplorer", { path: bundlePath }).catch((err: unknown) => {
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
        {value ?? "—"}
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
  const [, forceUpdate] = useReducer((n: number) => n + 1, 0);
  useEffect(() => {
    if (!running) return;
    const handle = setInterval(forceUpdate, 500);
    return () => clearInterval(handle);
  }, [running]);
}

function formatDurationStat(startedAt: number | null, finishedAt: number | null): string {
  if (startedAt === null) return "—";
  const end = finishedAt ?? Date.now();
  return formatDurationShort(end - startedAt);
}
