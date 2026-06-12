// Host-notification wiring for the compile store. Importing this module for
// side effects registers every `subscribe()` handler exactly once. The store
// imports it so any consumer that pulls in the store also pulls in the
// subscriptions.

import { rpcCall, subscribe } from "@shared/rpc";
import { useToastStore } from "@shared/toast";
import { buildSuccessDetail, formatDurationShort } from "./format";
import { useCompileStore, type CompileLogLevel } from "./store";

const LOG_LEVELS: readonly string[] = ["info", "warn", "error"];

/** The generated DTO types compileLog's level as a plain string; narrow it
 *  here so the store only ever holds the known levels. */
function parseLogLevel(level: string): CompileLogLevel {
  return LOG_LEVELS.includes(level) ? (level as CompileLogLevel) : "info";
}

subscribe("compileStarted", () => {
  useCompileStore.getState().handleStarted();
});

subscribe("compilePhase", (e) => {
  useCompileStore.getState().handlePhase(e.phase);
});

subscribe("compileStatus", (e) => {
  useCompileStore.getState().handleStatusLine(e.status);
});

subscribe("compileProgress", (e) => {
  useCompileStore.getState().handleProgress(e.current, e.total);
});

subscribe("compileLog", (e) => {
  useCompileStore.getState().handleLog(parseLogLevel(e.level), e.message);
});

subscribe("compileFinished", (e) => {
  useCompileStore
    .getState()
    .handleFinished(e.success, e.bundlePath ?? null, e.errorMessage ?? null);

  const s = useCompileStore.getState();
  const push = useToastStore.getState().push;

  if (e.success) {
    const bundlePath = s.bundlePath;
    const duration =
      s.startedAt !== null && s.finishedAt !== null
        ? formatDurationShort(s.finishedAt - s.startedAt)
        : null;
    const detail = buildSuccessDetail(duration, s.warnCount, bundlePath);
    push({
      variant: "success",
      message: "Compile complete",
      ...(detail !== null ? { detail } : {}),
      ...(bundlePath !== null
        ? {
            actions: [
              {
                label: "Reveal",
                run: () => {
                  rpcCall<null>("revealInExplorer", { path: bundlePath }).catch((err: unknown) => {
                    console.error("[Compile] reveal failed:", err);
                  });
                },
              },
            ],
          }
        : {}),
    });
  } else {
    push({
      variant: "error",
      message: "Compile failed",
      ...(s.errorMessage !== null ? { detail: s.errorMessage } : {}),
    });
  }
});
