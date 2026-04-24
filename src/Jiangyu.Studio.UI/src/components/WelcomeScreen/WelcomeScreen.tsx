import { useState, useEffect } from "react";
import { CircleX, TriangleAlert, Info, X } from "lucide-react";
import { rpcCall } from "@lib/rpc.ts";
import { pickProjectFolder } from "@lib/project/commands.ts";
import { loadRecentProjects, removeRecentProject } from "@lib/project/recent.ts";
import { Spinner } from "@components/Spinner/Spinner.tsx";
import { NewProjectDialog } from "@components/NewProjectDialog/NewProjectDialog.tsx";
import type { ConfigStatus } from "@lib/compile/configStatus.ts";
import styles from "./WelcomeScreen.module.css";

interface WelcomeScreenProps {
  onOpenProject: (path: string) => void;
}

export function WelcomeScreen({ onOpenProject }: WelcomeScreenProps) {
  const [config, setConfig] = useState<ConfigStatus | null>(null);
  const [recentProjects, setRecentProjects] = useState<readonly string[]>([]);
  const [showNewDialog, setShowNewDialog] = useState(false);

  useEffect(() => {
    void rpcCall<ConfigStatus>("getConfigStatus")
      .then(setConfig)
      .catch(() => {});
    setRecentProjects(loadRecentProjects());
  }, []);

  const handleRemoveRecent = (path: string) => {
    setRecentProjects(removeRecentProject(path));
  };

  const handleOpen = async () => {
    const path = await pickProjectFolder();
    if (path !== null) onOpenProject(path);
  };

  const handleSetGamePath = async () => {
    try {
      const result = await rpcCall<ConfigStatus | null>("setGamePath");
      if (result) setConfig(result);
    } catch (err) {
      console.error("[Studio] setGamePath failed:", err);
    }
  };

  const handleSetUnityPath = async () => {
    try {
      const result = await rpcCall<ConfigStatus | null>("setUnityEditorPath");
      if (result) setConfig(result);
    } catch (err) {
      console.error("[Studio] setUnityEditorPath failed:", err);
    }
  };

  const hasGameError = config?.gamePath === null;
  const hasEditorWarning = config?.unityEditorPath === null;
  const hasEditorVersionMismatch =
    config?.unityEditorPath != null && config.unityEditorError !== null;
  const hasMelonWarning = config?.melonLoaderError != null;

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
                      onClick={() => void handleSetGamePath()}
                    >
                      change
                    </button>
                  </div>
                  {!hasEditorWarning && !hasEditorVersionMismatch && (
                    <div className={styles.statusLine}>
                      <span className={styles.statusText}>Unity: {config.unityEditorPath}</span>
                      <button
                        className={styles.configLinkInline}
                        type="button"
                        onClick={() => void handleSetUnityPath()}
                      >
                        change
                      </button>
                    </div>
                  )}
                </div>
              )}

              {(hasGameError ||
                hasEditorWarning ||
                hasEditorVersionMismatch ||
                hasMelonWarning) && (
                <div className={styles.statusGroup}>
                  {hasGameError && (
                    <div className={styles.statusLine}>
                      <CircleX size={14} className={styles.iconError} />
                      <span className={styles.statusTextError}>MENACE not found</span>
                      <button
                        className={styles.setPathBtn}
                        type="button"
                        onClick={() => void handleSetGamePath()}
                      >
                        Set path…
                      </button>
                    </div>
                  )}
                  {hasEditorWarning && (
                    <div className={styles.statusLine}>
                      <TriangleAlert size={14} className={styles.iconWarning} />
                      <span className={styles.statusTextWarning}>Unity Editor not found</span>
                      <span
                        className={styles.infoTip}
                        title={
                          config.gameUnityVersion !== null
                            ? `Requires ${config.gameUnityVersion}. Only needed for asset compilation.`
                            : "Only needed for asset compilation."
                        }
                      >
                        <Info size={12} />
                      </span>
                      <button
                        className={styles.setPathBtn}
                        type="button"
                        onClick={() => void handleSetUnityPath()}
                      >
                        Set path…
                      </button>
                    </div>
                  )}
                  {hasEditorVersionMismatch && (
                    <div className={styles.statusLine}>
                      <TriangleAlert size={14} className={styles.iconWarning} />
                      <span className={styles.statusTextWarning}>{config.unityEditorError}</span>
                      <button
                        className={styles.configLinkInline}
                        type="button"
                        onClick={() => void handleSetUnityPath()}
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

          {config.gamePath !== null && (
            <div className={styles.actions}>
              <div className={styles.actionRow}>
                <button className={styles.action} type="button" onClick={() => void handleOpen()}>
                  Open Project
                </button>
                <button
                  className={styles.actionGhost}
                  type="button"
                  onClick={() => setShowNewDialog(true)}
                >
                  New Project
                </button>
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
