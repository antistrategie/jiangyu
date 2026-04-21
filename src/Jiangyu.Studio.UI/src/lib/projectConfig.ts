import { rpcCall } from "./rpc.ts";

export interface ProjectConfig {
  readonly assetExportPath?: string | null;
}

export function getProjectConfig(projectPath: string): Promise<ProjectConfig> {
  return rpcCall<ProjectConfig>("getProjectConfig", { projectPath });
}

export function setProjectAssetExportPath(
  projectPath: string,
  exportPath: string | null,
): Promise<ProjectConfig> {
  return rpcCall<ProjectConfig>("setProjectAssetExportPath", { projectPath, exportPath });
}
