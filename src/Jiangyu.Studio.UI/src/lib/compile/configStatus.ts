/**
 * Wire shape of the `getConfigStatus` RPC reply. The Studio Host side emits
 * these fields from GlobalConfig — see `RpcDispatcher.HandleGetConfigStatus`.
 *
 * UI consumers currently infer "missing" from `gamePath === null` rather than
 * surfacing the `gameError` / `unityEditorError` strings; the error fields stay
 * on the type anyway so the shape matches the wire exactly.
 */
export interface ConfigStatus {
  readonly gamePath: string | null;
  readonly gameError: string | null;
  readonly gameUnityVersion: string | null;
  readonly unityEditorPath: string | null;
  readonly unityEditorError: string | null;
  readonly unityEditorVersion: string | null;
  readonly melonLoaderError: string | null;
}
