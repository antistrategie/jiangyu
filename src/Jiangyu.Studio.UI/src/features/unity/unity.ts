import { rpcCall } from "@shared/rpc";
import type { UnityImportPrefabResult, UnityInitResult, UnityOpenResult } from "@shared/rpc";

export type { UnityImportPrefabResult, UnityInitResult, UnityOpenResult };

export interface UnityImportPrefabParams {
  readonly assetName: string;
  readonly pathId?: number;
  readonly collection?: string;
}

export async function unityInit(): Promise<UnityInitResult> {
  return rpcCall<UnityInitResult>("unityInit");
}

export async function unityOpen(): Promise<UnityOpenResult> {
  return rpcCall<UnityOpenResult>("unityOpen");
}

export async function unityImportPrefab(
  params: UnityImportPrefabParams,
): Promise<UnityImportPrefabResult> {
  return rpcCall<UnityImportPrefabResult>("unityImportPrefab", params);
}
