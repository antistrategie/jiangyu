import { rpcCall } from "@shared/rpc";

/**
 * Prompt the user for a project folder, starting the dialog in `initial`
 * when given. Returns null if cancelled.
 */
export async function pickProjectFolder(initial?: string | null): Promise<string | null> {
  try {
    return await rpcCall<string | null>("openFolder", initial == null ? {} : { initial }, {
      timeoutMs: 0,
    });
  } catch (err) {
    console.error("[project] openFolder failed:", err);
    return null;
  }
}
