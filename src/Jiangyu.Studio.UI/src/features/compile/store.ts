// Compile pipeline state. A Zustand store rather than component state so the
// status bar, the compile dossier, and the keyboard shortcut can each consume
// exactly the slices they need without routing every log line through App.

import { create } from "zustand";
import { rpcCall } from "@shared/rpc";
import { useToastStore } from "@shared/toast";
// Importing the module registers the host notification subscribers as a side
// effect. The circular reference is safe — subscriptions only reads
// useCompileStore lazily inside its subscribe callbacks.
import "./subscriptions";

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
  /** Most recent log entries, capped at MAX_RETAINED_LOGS. */
  readonly logs: readonly CompileLogEntry[];
  /** Entries evicted from `logs` once the cap was hit. */
  readonly droppedLogCount: number;
  /** Warning total across the whole run, including evicted entries. */
  readonly warnCount: number;
  /** Error total across the whole run, including evicted entries. */
  readonly errorCount: number;
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
  droppedLogCount: 0,
  warnCount: 0,
  errorCount: 0,
  bundlePath: null,
  errorMessage: null,
  startedAt: null,
  finishedAt: null,
};

/** Retention cap for the in-memory log list. The dossier renders the list
 *  unvirtualised, so an unbounded compile (Unity import spam) would grow the
 *  DOM without limit; older lines are dropped and counted instead. */
export const MAX_RETAINED_LOGS = 2000;

let logIdCounter = 0;

interface CompileStore extends CompileState {
  /** Kick off a compile via the host RPC. State flips to running
   *  immediately; failures surface as a failed state plus an error toast. */
  readonly start: () => void;
  readonly reset: () => void;
  readonly handleStarted: () => void;
  readonly handlePhase: (phase: string) => void;
  readonly handleStatusLine: (statusLine: string) => void;
  readonly handleProgress: (current: number, total: number) => void;
  readonly handleLog: (level: CompileLogLevel, message: string) => void;
  readonly handleFinished: (
    success: boolean,
    bundlePath: string | null,
    errorMessage: string | null,
  ) => void;
}

export const useCompileStore = create<CompileStore>((set) => ({
  ...INITIAL_COMPILE_STATE,

  start: () => {
    logIdCounter = 0;
    set({ ...INITIAL_COMPILE_STATE, status: "running", startedAt: Date.now() });
    rpcCall<{ started: boolean }>("compile").catch((err: unknown) => {
      const message = err instanceof Error ? err.message : String(err);
      set({ status: "failed", errorMessage: message, finishedAt: Date.now() });
      useToastStore.getState().push({
        variant: "error",
        message: "Compile failed",
        detail: message,
      });
    });
  },

  reset: () => {
    logIdCounter = 0;
    set(INITIAL_COMPILE_STATE);
  },

  // Mark start time + flip to "running" so the status bar lights up
  // regardless of who kicked off the compile (the UI button via start()
  // already does this; agent-triggered compiles via the MCP override come
  // straight to this notification with no prior state change). Also resets
  // the accumulators so a fresh run doesn't carry stale logs.
  handleStarted: () => {
    logIdCounter = 0;
    set({ ...INITIAL_COMPILE_STATE, status: "running", startedAt: Date.now() });
  },

  handlePhase: (phase) => set({ phase, statusLine: null }),

  handleStatusLine: (statusLine) => set({ statusLine }),

  handleProgress: (current, total) => set({ progress: total === 0 ? null : { current, total } }),

  handleLog: (level, message) =>
    set((s) => {
      const entry: CompileLogEntry = { id: ++logIdCounter, level, message };
      const overflow = s.logs.length >= MAX_RETAINED_LOGS;
      return {
        logs: overflow
          ? [...s.logs.slice(s.logs.length - MAX_RETAINED_LOGS + 1), entry]
          : [...s.logs, entry],
        droppedLogCount: overflow ? s.droppedLogCount + 1 : s.droppedLogCount,
        warnCount: level === "warn" ? s.warnCount + 1 : s.warnCount,
        errorCount: level === "error" ? s.errorCount + 1 : s.errorCount,
      };
    }),

  handleFinished: (success, bundlePath, errorMessage) =>
    set({
      status: success ? "success" : "failed",
      bundlePath,
      errorMessage,
      progress: null,
      finishedAt: Date.now(),
    }),
}));
