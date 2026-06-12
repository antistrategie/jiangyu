import { useState } from "react";
import { CircleX, TriangleAlert, Info, X } from "lucide-react";
import { rpcCall } from "@shared/rpc";
import { pickProjectFolder } from "@features/project/commands";
import { loadRecentProjects, removeRecentProject } from "@features/project/recent";
import { useConfigStatus } from "@features/settings/useConfigStatus";
import { Spinner } from "@shared/ui/Spinner/Spinner";
import { Button } from "@shared/ui/Button/Button";
import { NewProjectDialog } from "@features/project/NewProjectDialog/NewProjectDialog";
import styles from "./WelcomeScreen.module.css";

interface WelcomeScreenProps {
  onOpenProject: (path: string) => void;
}

export function WelcomeScreen({ onOpenProject }: WelcomeScreenProps) {
  const { config, pickGamePath, pickUnityPath } = useConfigStatus();
  const [recentProjects, setRecentProjects] = useState<readonly string[]>(loadRecentProjects);
  const [showNewDialog, setShowNewDialog] = useState(false);

  const handleRemoveRecent = (path: string) => {
    setRecentProjects(removeRecentProject(path));
  };

  const handleOpen = async () => {
    const path = await pickProjectFolder();
    if (path !== null) onOpenProject(path);
  };

  const hasGameError = config?.gamePath === null;
  const hasEditorError = config?.unityEditorPath === null;
  const hasEditorVersionMismatch =
    config?.unityEditorPath != null && config.unityEditorError !== null;
  const hasMelonWarning = config?.melonLoaderError != null;
  const canOpenProject =
    config?.gamePath != null && config.unityEditorPath != null && config.unityEditorError === null;

  return (
    <main className={styles.welcome}>
      <div className={styles.hero}>
        <span className={styles.glyph}>绛雨</span>
        <img className={styles.stroke} src="/stroke.png" alt="" aria-hidden="true" />
      </div>
      <p className={styles.subtitle}>Jiangyu Studio</p>

      {config === null ? (
        <div className={styles.splashLoading}>
          <Spinner size={14} />
          <span>Loading</span>
        </div>
      ) : (
        <>
          <details className={styles.configDetails}>
            <summary className={styles.configSummary}>Configuration</summary>
            <div className={styles.configContent}>
              {config.gamePath !== null && (
                <div className={styles.statusGroup}>
                  <div className={styles.statusLine}>
                    <span className={styles.statusText}>MENACE: {config.gamePath}</span>
                    <button
                      className={styles.configLinkInline}
                      type="button"
                      onClick={pickGamePath}
                    >
                      change
                    </button>
                  </div>
                  {!hasEditorError && !hasEditorVersionMismatch && (
                    <div className={styles.statusLine}>
                      <span className={styles.statusText}>Unity: {config.unityEditorPath}</span>
                      <button
                        className={styles.configLinkInline}
                        type="button"
                        onClick={pickUnityPath}
                      >
                        change
                      </button>
                    </div>
                  )}
                </div>
              )}

              {(hasGameError || hasEditorError || hasEditorVersionMismatch || hasMelonWarning) && (
                <div className={styles.statusGroup}>
                  {hasGameError && (
                    <div className={styles.statusLine}>
                      <CircleX size={14} className={styles.iconError} />
                      <span className={styles.statusTextError}>MENACE not found</span>
                      <button className={styles.setPathBtn} type="button" onClick={pickGamePath}>
                        Set path…
                      </button>
                    </div>
                  )}
                  {hasEditorError && (
                    <div className={styles.statusLine}>
                      <CircleX size={14} className={styles.iconError} />
                      <span className={styles.statusTextError}>Unity Editor not found</span>
                      {config.gameUnityVersion !== null && (
                        <span
                          className={styles.infoTip}
                          title={`Requires ${config.gameUnityVersion}.`}
                        >
                          <Info size={12} />
                        </span>
                      )}
                      <button className={styles.setPathBtn} type="button" onClick={pickUnityPath}>
                        Set path…
                      </button>
                    </div>
                  )}
                  {hasEditorVersionMismatch && (
                    <div className={styles.statusLine}>
                      <CircleX size={14} className={styles.iconError} />
                      <span className={styles.statusTextError}>{config.unityEditorError}</span>
                      <button
                        className={styles.configLinkInline}
                        type="button"
                        onClick={pickUnityPath}
                      >
                        change
                      </button>
                    </div>
                  )}
                  {hasMelonWarning && (
                    <div className={styles.statusLine}>
                      <TriangleAlert size={14} className={styles.iconWarning} />
                      <span className={styles.statusTextWarning}>MelonLoader not installed</span>
                      <span
                        className={styles.infoTip}
                        title="Required to run mods in-game. Install MelonLoader into the MENACE directory."
                      >
                        <Info size={12} />
                      </span>
                    </div>
                  )}
                </div>
              )}
            </div>
          </details>

          {canOpenProject && (
            <div className={styles.actions}>
              <div className={styles.actionRow}>
                <Button variant="primary" size="md" onClick={() => void handleOpen()}>
                  Open Project
                </Button>
                <Button variant="ghost" size="md" onClick={() => setShowNewDialog(true)}>
                  New Project
                </Button>
              </div>
              {recentProjects.length > 0 && (
                <ul className={styles.recentList}>
                  {recentProjects.slice(0, 5).map((p) => (
                    <li key={p} className={styles.recentRow}>
                      <button
                        type="button"
                        className={styles.recentLink}
                        onClick={() => {
                          void rpcCall<string>("openProject", { path: p })
                            .then(onOpenProject)
                            .catch((err: unknown) => {
                              console.error("[Studio] openProject failed:", err);
                            });
                        }}
                      >
                        {p}
                      </button>
                      <button
                        type="button"
                        className={styles.recentRemove}
                        aria-label={`Remove ${p} from recent projects`}
                        title="Remove from recent"
                        onClick={() => handleRemoveRecent(p)}
                      >
                        <X size={12} />
                      </button>
                    </li>
                  ))}
                </ul>
              )}
            </div>
          )}
        </>
      )}
      {showNewDialog && (
        <NewProjectDialog onCreated={onOpenProject} onCancel={() => setShowNewDialog(false)} />
      )}
    </main>
  );
}
