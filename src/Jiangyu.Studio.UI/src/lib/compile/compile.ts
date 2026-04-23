import { useCallback, useEffect, useRef, useState } from "react";
import { rpcCall, subscribe } from "@lib/rpc.ts";
import { useToast } from "@lib/toast/toast.tsx";

export interface CompileSummary {
  readonly modName: string | null;
  readonly modVersion: string | null;
  readonly modAuthor: string | null;
  readonly modelReplacements: number;
  readonly textureReplacements: number;
  readonly spriteReplacements: number;
  readonly audioReplacements: number;
  readonly additionFiles: number;
  readonly templateFiles: number;
  readonly templatePatches: number;
  readonly templateClones: number;
}

export function useCompileSummary(enabled: boolean): CompileSummary | null {
  const [summary, setSummary] = useState<CompileSummary | null>(null);
  useEffect(() => {
    if (!enabled) return;
    let cancelled = false;
    rpcCall<CompileSummary>("getCompileSummary")
      .then((s) => {
        if (!cancelled) setSummary(s);
      })
      .catch((err: unknown) => {
        console.error("[Compile] getCompileSummary failed:", err);
      });
    return () => {
      cancelled = true;
    };
  }, [enabled]);
  return summary;
}

export type CompileStatus = "idle" | "running" | "success" | "failed";
export type CompileLogLevel = "info" | "warn" | "error";

export interface CompileLogEntry {
  readonly id: number;
  readonly level: CompileLogLevel;
  readonly message: string;
}

export interface CompileProgress {
  readonly current: number;
  readonly total: number;
}

export interface CompileState {
  readonly status: CompileStatus;
  readonly phase: string | null;
  readonly statusLine: string | null;
  readonly progress: CompileProgress | null;
  readonly logs: readonly CompileLogEntry[];
  readonly bundlePath: string | null;
  readonly errorMessage: string | null;
  readonly startedAt: number | null;
  readonly finishedAt: number | null;
}

export const INITIAL_COMPILE_STATE: CompileState = {
  status: "idle",
  phase: null,
  statusLine: null,
  progress: null,
  logs: [],
  bundlePath: null,
  errorMessage: null,
  startedAt: null,
  finishedAt: null,
};

interface CompileStartedEvent {
  readonly projectRoot: string;
}
interface CompilePhaseEvent {
  readonly phase: string;
}
interface CompileStatusEvent {
  readonly status: string;
}
interface CompileProgressEvent {
  readonly current: number;
  readonly total: number;
}
interface CompileLogEvent {
  readonly level: CompileLogLevel;
  readonly message: string;
}
interface CompileFinishedEvent {
  readonly success: boolean;
  readonly bundlePath?: string | null;
  readonly errorMessage?: string | null;
}

export interface UseCompile {
  readonly state: CompileState;
  readonly start: () => void;
  readonly reset: () => void;
}

export function useCompile(): UseCompile {
  const [state, setState] = useState<CompileState>(INITIAL_COMPILE_STATE);
  const logIdRef = useRef(0);
  const warnCountRef = useRef(0);
  const startedAtRef = useRef<number | null>(null);
  const { push: pushToast } = useToast();
  const pushToastRef = useRef(pushToast);
  useEffect(() => {
    pushToastRef.current = pushToast;
  }, [pushToast]);

  useEffect(() => {
    const unsubStarted = subscribe<CompileStartedEvent>("compileStarted", () => {
      // Mark start time here so it reflects the host-side kickoff, not the
      // request serialisation on the client.
      const now = Date.now();
      startedAtRef.current = now;
      setState((prev) => ({ ...prev, startedAt: now }));
    });
    const unsubPhase = subscribe<CompilePhaseEvent>("compilePhase", (e) => {
      setState((prev) => ({ ...prev, phase: e.phase, statusLine: null }));
    });
    const unsubStatus = subscribe<CompileStatusEvent>("compileStatus", (e) => {
      setState((prev) => ({ ...prev, statusLine: e.status }));
    });
    const unsubProgress = subscribe<CompileProgressEvent>("compileProgress", (e) => {
      setState((prev) => ({
        ...prev,
        progress: e.total === 0 ? null : { current: e.current, total: e.total },
      }));
    });
    const unsubLog = subscribe<CompileLogEvent>("compileLog", (e) => {
      if (e.level === "warn") warnCountRef.current += 1;
      setState((prev) => ({
        ...prev,
        logs: [...prev.logs, { id: ++logIdRef.current, level: e.level, message: e.message }],
      }));
    });
    const unsubFinished = subscribe<CompileFinishedEvent>("compileFinished", (e) => {
      const finishedAt = Date.now();
      setState((prev) => ({
        ...prev,
        status: e.success ? "success" : "failed",
        bundlePath: e.bundlePath ?? null,
        errorMessage: e.errorMessage ?? null,
        progress: null,
        finishedAt,
      }));

      const warnings = warnCountRef.current;
      const duration =
        startedAtRef.current !== null
          ? formatDurationShort(finishedAt - startedAtRef.current)
          : null;
      const toast = pushToastRef.current;

      if (e.success) {
        const bundlePath = e.bundlePath ?? null;
        const detail = buildSuccessDetail(duration, warnings, bundlePath);
        toast({
          variant: "success",
          message: bundlePath !== null ? "Compile complete" : "Compile complete (template-only)",
          ...(detail !== null ? { detail } : {}),
          ...(bundlePath !== null
            ? {
                actions: [
                  {
                    label: "Reveal",
                    run: () => {
                      rpcCall<null>("revealInExplorer", { path: bundlePath }).catch(
                        (err: unknown) => {
                          console.error("[Compile] reveal failed:", err);
                        },
                      );
                    },
                  },
                ],
              }
            : {}),
        });
      } else {
        toast({
          variant: "error",
          message: "Compile failed",
          ...(e.errorMessage !== null && e.errorMessage !== undefined
            ? { detail: e.errorMessage }
            : {}),
        });
      }
    });
    return () => {
      unsubStarted();
      unsubPhase();
      unsubStatus();
      unsubProgress();
      unsubLog();
      unsubFinished();
    };
  }, []);

  const start = useCallback(() => {
    logIdRef.current = 0;
    warnCountRef.current = 0;
    startedAtRef.current = Date.now();
    setState({
      ...INITIAL_COMPILE_STATE,
      status: "running",
      startedAt: startedAtRef.current,
    });
    rpcCall<{ started: boolean }>("compile").catch((err: unknown) => {
      const message = err instanceof Error ? err.message : String(err);
      const finishedAt = Date.now();
      setState((prev) => ({
        ...prev,
        status: "failed",
        errorMessage: message,
        finishedAt,
      }));
      pushToastRef.current({
        variant: "error",
        message: "Compile failed",
        detail: message,
      });
    });
  }, []);

  const reset = useCallback(() => {
    logIdRef.current = 0;
    warnCountRef.current = 0;
    startedAtRef.current = null;
    setState(INITIAL_COMPILE_STATE);
  }, []);

  return { state, start, reset };
}

export function buildSuccessDetail(
  duration: string | null,
  warnings: number,
  bundlePath: string | null,
): string | null {
  const parts: string[] = [];
  if (duration !== null) parts.push(duration);
  if (warnings > 0) parts.push(`${warnings} warning${warnings === 1 ? "" : "s"}`);
  if (bundlePath !== null) parts.push(bundlePath);
  return parts.length === 0 ? null : parts.join(" · ");
}

export function formatDurationShort(ms: number): string {
  const total = Math.floor(ms / 1000);
  const mins = Math.floor(total / 60);
  const secs = total % 60;
  if (mins === 0) return `${secs}s`;
  return `${mins}m${secs.toString().padStart(2, "0")}s`;
}
