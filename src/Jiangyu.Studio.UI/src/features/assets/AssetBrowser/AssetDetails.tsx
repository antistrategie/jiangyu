// Single-asset detail surface for the AssetBrowser: preview viewer, header
// with the export/import action menu, and the metadata block. Renders only
// when the browser has a focused row.

import { useMemo } from "react";
import {
  buildReplacementPath,
  countAssetInstances,
  isAssetNameUnique,
  isAudioClip,
  isSprite,
  type AssetEntry,
} from "@features/assets/assets";
import type { AssetPreviewResult } from "@shared/rpc";
import { Button } from "@shared/ui/Button/Button";
import { DetailTitle, MetaBlock, MetaRow } from "@shared/ui/DetailPanel/DetailPanel";
import {
  MenuFooter,
  MenuItem,
  MenuItemContent,
  MenuItemLabel,
  MenuItemSubtext,
  MenuList,
  MenuListBody,
  MenuSeparator,
} from "@shared/ui/MenuList/MenuList";
import { Spinner } from "@shared/ui/Spinner/Spinner";
import { AudioPlayer } from "./AudioPlayer";
import { BatchProgressBar, type BatchProgress } from "./BatchProgressBar";
import { ImageViewer } from "./ImageViewer";
import { ModelViewer } from "./ModelViewer";
import styles from "./AssetBrowser.module.css";

export type Destination = "default" | "project" | "custom";

interface AssetDetailsProps {
  readonly focused: AssetEntry | null;
  readonly previewData: AssetPreviewResult | null;
  readonly previewLoading: boolean;
  readonly nameUniqueness: ReadonlyMap<string, number>;
  readonly actionMenuRef: React.RefObject<HTMLDivElement | null>;
  readonly actionMenuOpen: boolean;
  readonly onToggleActionMenu: () => void;
  readonly onCloseActionMenu: () => void;
  readonly onExport: (dest: Destination) => void;
  readonly onImport: () => void;
  readonly onConfigureProjectPath: () => void;
  readonly projectExportPath: string | null;
  readonly selectedCount: number;
  readonly exporting: boolean;
  readonly exportProgress: BatchProgress | null;
  readonly importing: boolean;
  readonly importProgress: BatchProgress | null;
  readonly allSelectedArePrefabs: boolean;
}

export function AssetDetails({
  focused,
  previewData,
  previewLoading,
  nameUniqueness,
  actionMenuRef,
  actionMenuOpen,
  onToggleActionMenu,
  onCloseActionMenu,
  onExport,
  onImport,
  onConfigureProjectPath,
  projectExportPath,
  selectedCount,
  exporting,
  exportProgress,
  importing,
  importProgress,
  allSelectedArePrefabs,
}: AssetDetailsProps) {
  // Memoise so the megabyte-class base64 isn't re-concatenated each render.
  // The image/audio src reset logic in those viewers compares by string
  // value, so identity doesn't matter for correctness — only for avoiding
  // wasted work.
  const previewDataUrl = useMemo(
    () => (previewData ? `data:${previewData.mimeType};base64,${previewData.data}` : ""),
    [previewData],
  );

  if (focused === null) {
    return <div className={styles.detailsEmpty}>Select an asset to see details</div>;
  }

  let preview: React.ReactNode;
  if (previewData?.mimeType === "image/png") {
    preview = <ImageViewer src={previewDataUrl} alt={focused.name ?? "Asset preview"} />;
  } else if (previewData?.mimeType.startsWith("audio/")) {
    preview = <AudioPlayer src={previewDataUrl} />;
  } else if (previewData?.mimeType === "model/gltf-binary") {
    preview = <ModelViewer base64={previewData.data} />;
  } else if (previewLoading) {
    preview = <Spinner />;
  } else {
    preview = <span className={styles.previewPlaceholder}>No preview</span>;
  }

  const unique = isAssetNameUnique(focused, nameUniqueness);
  const replacementPath = buildReplacementPath(focused, unique);
  const instanceCount = countAssetInstances(focused, nameUniqueness);
  const showAffects =
    !unique &&
    instanceCount > 1 &&
    (focused.className === "Texture2D" ||
      focused.className === "Sprite" ||
      focused.className === "AudioClip");

  return (
    <>
      <div className={styles.preview}>{preview}</div>
      <div className={styles.detailHeader}>
        <div className={styles.detailTitleRow}>
          <DetailTitle>{focused.name ?? "(unnamed)"}</DetailTitle>
          <div className={styles.exportDropdown} ref={actionMenuRef}>
            <Button
              variant="primary"
              disabled={selectedCount === 0 || exporting || importing}
              onClick={onToggleActionMenu}
            >
              {(exporting || importing) && <Spinner size={12} />}
              <span>
                {exporting
                  ? "Exporting…"
                  : importing
                    ? "Importing…"
                    : `Actions on ${selectedCount || "0"} selected`}
              </span>
              {!exporting && !importing && (
                <span className={styles.exportButtonChevron} aria-hidden>
                  ▾
                </span>
              )}
            </Button>
            {actionMenuOpen && (
              <MenuList className={styles.exportMenuPos}>
                <MenuListBody>
                  <MenuItem
                    onClick={() => {
                      onCloseActionMenu();
                      onExport("default");
                    }}
                  >
                    <MenuItemContent>
                      <MenuItemLabel>Export to default</MenuItemLabel>
                      <MenuItemSubtext>exported/</MenuItemSubtext>
                    </MenuItemContent>
                  </MenuItem>
                  <MenuItem
                    disabled={projectExportPath === null}
                    onClick={() => {
                      onCloseActionMenu();
                      onExport("project");
                    }}
                  >
                    <MenuItemContent>
                      <MenuItemLabel>Export to project defined</MenuItemLabel>
                      <MenuItemSubtext>{projectExportPath ?? "not configured"}</MenuItemSubtext>
                    </MenuItemContent>
                  </MenuItem>
                  <MenuItem
                    onClick={() => {
                      onCloseActionMenu();
                      onExport("custom");
                    }}
                  >
                    <MenuItemLabel>Export to custom…</MenuItemLabel>
                  </MenuItem>
                  {allSelectedArePrefabs && (
                    <>
                      <MenuSeparator />
                      <MenuItem
                        onClick={() => {
                          onCloseActionMenu();
                          onImport();
                        }}
                      >
                        <MenuItemContent>
                          <MenuItemLabel>Import to Unity</MenuItemLabel>
                          <MenuItemSubtext>unity/Assets/Imported/</MenuItemSubtext>
                        </MenuItemContent>
                      </MenuItem>
                    </>
                  )}
                </MenuListBody>
                <MenuFooter
                  onClick={() => {
                    onCloseActionMenu();
                    onConfigureProjectPath();
                  }}
                >
                  {projectExportPath ? "Change project path…" : "Configure project path…"}
                </MenuFooter>
              </MenuList>
            )}
          </div>
        </div>
        <MetaBlock>
          <MetaRow label="Class" value={focused.className ?? "—"} />
          <MetaRow label="Collection" value={focused.collection ?? "—"} />
          <MetaRow label="Path ID" value={String(focused.pathId)} />
          {isAudioClip(focused) && focused.audioFrequency != null && (
            <MetaRow label="Frequency" value={`${focused.audioFrequency} Hz`} />
          )}
          {isAudioClip(focused) && focused.audioChannels != null && (
            <MetaRow label="Channels" value={String(focused.audioChannels)} />
          )}
          {isSprite(focused) && focused.spriteBackingTextureName && (
            <MetaRow label="Atlas" value={focused.spriteBackingTextureName} />
          )}
          {isSprite(focused) &&
            focused.spriteTextureRectWidth != null &&
            focused.spriteTextureRectHeight != null && (
              <MetaRow
                label="Rect"
                value={`${String(Math.round(focused.spriteTextureRectWidth))} × ${String(Math.round(focused.spriteTextureRectHeight))} px`}
              />
            )}
          {replacementPath != null && <MetaRow label="Replace" value={replacementPath} />}
          {showAffects && <MetaRow label="Affects" value={`${String(instanceCount)} instances`} />}
        </MetaBlock>
        <BatchProgressBar progress={exportProgress} active={exporting} />
        <BatchProgressBar progress={importProgress} active={importing} />
      </div>
    </>
  );
}
