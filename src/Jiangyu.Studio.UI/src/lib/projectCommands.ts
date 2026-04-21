import { rpcCall } from "./rpc.ts";

/** Prompt the user for a project folder. Returns null if cancelled. */
export async function pickProjectFolder(): Promise<string | null> {
  try {
    return await rpcCall<string | null>("openFolder");
  } catch (err) {
    console.error("[project] openFolder failed:", err);
    return null;
  }
}
