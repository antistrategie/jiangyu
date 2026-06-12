import { useEffect, useState } from "react";
import { rpcCall } from "@shared/rpc";

export { buildSuccessDetail, formatDurationShort } from "./format";
export {
  INITIAL_COMPILE_STATE,
  MAX_RETAINED_LOGS,
  useCompileStore,
  type CompileLogEntry,
  type CompileLogLevel,
  type CompileProgress,
  type CompileState,
  type CompileStatus,
} from "./store";

export interface CompileSummary {
  readonly modName: string | null;
  readonly modVersion: string | null;
  readonly modAuthor: string | null;
  readonly models: number;
  readonly textures: number;
  readonly sprites: number;
  readonly audio: number;
  readonly replacementFiles: number;
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
