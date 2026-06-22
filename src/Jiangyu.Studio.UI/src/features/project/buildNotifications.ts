// Host-notification wiring for the non-blocking build-output operations. Packaging and
// deploying run on a host worker thread (so the UI doesn't freeze while zipping/copying) and
// report completion via the packageFinished / deployFinished notifications. Importing this
// module for side effects registers the subscribers once; App pulls it in at startup.

import { rpcCall, subscribe } from "@shared/rpc";
import { useToastStore } from "@shared/toast";

function reveal(path: string) {
  rpcCall<null>("revealInExplorer", { path }).catch((err: unknown) => {
    console.error("[Build] reveal failed:", err);
  });
}

// Shared success/error toast for a finished build operation. On success, surfaces the
// resulting path with a Reveal action; on failure, surfaces the error detail.
function buildFinishedToast(
  success: boolean,
  errorMessage: string | null | undefined,
  failTitle: string,
  successMessage: string,
  revealPath: string | null | undefined,
): void {
  const push = useToastStore.getState().push;
  if (!success) {
    push({
      variant: "error",
      message: failTitle,
      ...(errorMessage != null ? { detail: errorMessage } : {}),
    });
    return;
  }

  const path = revealPath;
  push({
    variant: "success",
    message: successMessage,
    ...(path != null
      ? { detail: path, actions: [{ label: "Reveal", run: () => reveal(path) }] }
      : {}),
  });
}

subscribe("packageFinished", (e) => {
  buildFinishedToast(
    e.success,
    e.errorMessage,
    "Package failed",
    `Packaged ${e.modName ?? ""} ${e.version ?? ""}`.trim(),
    e.archivePath,
  );
});

subscribe("deployFinished", (e) => {
  buildFinishedToast(
    e.success,
    e.errorMessage,
    "Deploy failed",
    `Deployed ${e.modName ?? ""}`.trim(),
    e.destDir,
  );
});
