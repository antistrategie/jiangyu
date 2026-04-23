import { useState, useEffect } from "react";
import { CircleX, TriangleAlert, Info, X } from "lucide-react";
import { rpcCall } from "../../lib/rpc.ts";
import { pickProjectFolder } from "../../lib/projectCommands.ts";
import { loadRecentProjects, removeRecentProject } from "../../lib/recentProjects.ts";
import { Spinner } from "../Spinner/Spinner.tsx";
import { NewProjectDialog } from "../NewProjectDialog/NewProjectDialog.tsx";
import type { ConfigStatus } from "../../lib/configStatus.ts";
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

  const hasGameError = config !== null && config.gamePath === null;
  const hasEditorWarning = config !== null && config.unityEditorPath === null;
  const hasEditorVersionMismatch =
    config !== null && config.unityEditorPath !== null && config.unityEditorError !== null;
  const hasMelonWarning = config !== null && config.melonLoaderError !== null;

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
              {config !== null && config.gamePath !== null && (
                <div className={styles.statusGroup}>
                  <div className={styles.statusLine}>
                    <span className={styles.statusText}>MENACE: {config.gamePath}</span>
                    <button
                      className={styles.configLinkInline}
                      type="button"
                      onClick={handleSetGamePath}
                    >
                      change
                    </button>
                  </div>
                  {!hasEditorWarning && !hasEditorVersionMismatch && (
                    <div className={styles.statusLine}>
                      <span className={styles.statusText}>
                        Unity:{" "}
                        {config.unityEditorPath ?? <em className={styles.notSet}>not set</em>}
                      </span>
                      <button
                        className={styles.configLinkInline}
                        type="button"
                        onClick={handleSetUnityPath}
                      >
                        {config.unityEditorPath ? "change" : "set"}
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
                        onClick={handleSetGamePath}
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
                        onClick={handleSetUnityPath}
                      >
                        Set path…
                      </button>
                    </div>
                  )}
                  {hasEditorVersionMismatch &&
                    config !== null &&
                    config.unityEditorError !== null && (
                      <div className={styles.statusLine}>
                        <TriangleAlert size={14} className={styles.iconWarning} />
                        <span className={styles.statusTextWarning}>{config.unityEditorError}</span>
                        <button
                          className={styles.configLinkInline}
                          type="button"
                          onClick={handleSetUnityPath}
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

          {config !== null && config.gamePath !== null && (
            <div className={styles.actions}>
              <div className={styles.actionRow}>
                <button className={styles.action} type="button" onClick={handleOpen}>
                  Open Project
                </button>
                <button className={styles.actionGhost} type="button" onClick={() => setShowNewDialog(true)}>
                  New Project
                </button>
              </div>
              {recentProjects.length > 0 && (
                <ul className={styles.recentList}>
                  {recentProjects.slice(0, 5).map((p) => (
                    <li key={p} className={styles.recentRow}>
                      <a
                        className={styles.recentLink}
                        href="#"
                        onClick={(e) => {
                          e.preventDefault();
                          void rpcCall<string>("openProject", { path: p })
                            .then(onOpenProject)
                            .catch((err) => {
                              console.error("[Studio] openProject failed:", err);
                            });
                        }}
                      >
                        {p}
                      </a>
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
        <NewProjectDialog
          onCreated={onOpenProject}
          onCancel={() => setShowNewDialog(false)}
        />
      )}
    </main>
  );
}
