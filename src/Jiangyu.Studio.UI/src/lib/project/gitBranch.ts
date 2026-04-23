import { useEffect, useState } from "react";
import { rpcCall } from "@lib/rpc.ts";

/**
 * Resolves the current git branch for a project root. Returns null when
 * git isn't installed, the path isn't a git repo, HEAD is detached, or any
 * other error — the indicator should just disappear in those cases rather
 * than surface a "couldn't read branch" message.
 *
 * Fetches on project change and on window focus. The focus trigger covers
 * the common case of switching branches in a terminal and alt-tabbing
 * back: the indicator picks up the change without per-file watching on
 * .git/HEAD.
 */
export function useGitBranch(projectPath: string | null): string | null {
  const [branch, setBranch] = useState<string | null>(null);

  useEffect(() => {
    if (projectPath === null) {
      setBranch(null);
      return;
    }
    let cancelled = false;

    const fetchBranch = () => {
      rpcCall<string | null>("getGitBranch", { path: projectPath })
        .then((result) => {
          if (!cancelled) setBranch(result);
        })
        .catch(() => {
          if (!cancelled) setBranch(null);
        });
    };

    fetchBranch();
    window.addEventListener("focus", fetchBranch);

    return () => {
      cancelled = true;
      window.removeEventListener("focus", fetchBranch);
    };
  }, [projectPath]);

  return branch;
}
