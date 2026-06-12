import { useCallback, useEffect, useState } from "react";
import { rpcCall } from "@shared/rpc";
import type { ConfigStatus } from "@features/compile/configStatus";

export interface ConfigStatusHandle {
  readonly config: ConfigStatus | null;
  /** Error from the mount-time fetch; pick handlers log instead. */
  readonly loadError: string | null;
  readonly setConfig: React.Dispatch<React.SetStateAction<ConfigStatus | null>>;
  /** Open the host's folder picker for the game install directory. */
  readonly pickGamePath: () => void;
  /** Open the host's file picker for the Unity Editor binary. */
  readonly pickUnityPath: () => void;
}

/**
 * Fetch the host config status on mount and expose the set-path pickers.
 * Shared by the settings Paths section and the welcome screen so the fetch
 * guard and picker plumbing exist once.
 */
export function useConfigStatus(): ConfigStatusHandle {
  const [config, setConfig] = useState<ConfigStatus | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    rpcCall<ConfigStatus>("getConfigStatus")
      .then((status) => {
        if (!cancelled) setConfig(status);
      })
      .catch((err: unknown) => {
        if (!cancelled) setLoadError(err instanceof Error ? err.message : String(err));
      });
    return () => {
      cancelled = true;
    };
  }, []);

  // The pickers resolve null when the user cancels the host dialog; only a
  // confirmed pick updates the status.
  const pickGamePath = useCallback(() => {
    rpcCall<ConfigStatus | null>("setGamePath")
      .then((result) => {
        if (result) setConfig(result);
      })
      .catch((err: unknown) => {
        console.error("[Settings] setGamePath failed:", err);
      });
  }, []);

  const pickUnityPath = useCallback(() => {
    rpcCall<ConfigStatus | null>("setUnityEditorPath")
      .then((result) => {
        if (result) setConfig(result);
      })
      .catch((err: unknown) => {
        console.error("[Settings] setUnityEditorPath failed:", err);
      });
  }, []);

  return { config, loadError, setConfig, pickGamePath, pickUnityPath };
}
