import { useState } from "react";
import { CircleX, TriangleAlert } from "lucide-react";
import { rpcCall } from "@shared/rpc";
import type { LoaderDeployResult } from "@shared/rpc";
import type { ConfigStatus } from "@features/compile/configStatus";
import { useConfigStatus } from "@features/settings/useConfigStatus";
import { bridgeSetEnabled } from "@features/bridge/bridge";
import { useBridgeStatus } from "@features/bridge/useBridgeStatus";
import { SegmentedControl } from "@shared/ui/SegmentedControl/SegmentedControl";
import { Field, SectionHeader } from "./FormPrimitives";
import styles from "./SettingsModal.module.css";

export function PathsSection() {
  const { config, loadError, setConfig, pickGamePath, pickUnityPath } = useConfigStatus();

  return (
    <>
      <SectionHeader title="Game · 游戏" />
      {loadError !== null && <p className={styles.error}>Failed to load config: {loadError}</p>}
      {config === null && loadError === null && <p className={styles.muted}>Loading…</p>}
      {config !== null && (
        <>
          <PathRow
            label="MENACE"
            hint="Game installation directory"
            path={config.gamePath}
            missingIcon={<CircleX size={12} />}
            onChange={pickGamePath}
          />
          <PathRow
            label="Unity Editor"
            hint={
              config.gameUnityVersion !== null
                ? `Unity Editor ${config.gameUnityVersion}`
                : "Unity Editor binary"
            }
            path={config.unityEditorPath}
            missingIcon={<CircleX size={12} />}
            onChange={pickUnityPath}
          />
          {config.unityEditorPath !== null && config.unityEditorError !== null && (
            <p className={styles.error}>
              <CircleX size={12} /> {config.unityEditorError}
            </p>
          )}
          {config.melonLoaderError !== null && (
            <p className={styles.warning}>
              <TriangleAlert size={12} /> MelonLoader: {config.melonLoaderError}
            </p>
          )}
          {config.gamePath !== null && (
            <LoaderField
              config={config}
              onDeployed={(variant, version) =>
                setConfig({
                  ...config,
                  deployedLoaderVariant: variant,
                  deployedLoaderVersion: version,
                })
              }
            />
          )}
          <BridgeControls loaderVariant={config.deployedLoaderVariant} />
        </>
      )}
    </>
  );
}

// --- In-game loader deploy -------------------------------------------------

// The deployed variant + version are detected from Mods/Jiangyu.Loader.dll, not a
// stored setting. Each button deploys (or, when it is already the deployed variant,
// updates) that build; the dev build adds the Studio bridge and diagnostic probes.
function LoaderField({
  config,
  onDeployed,
}: {
  readonly config: ConfigStatus;
  readonly onDeployed: (variant: string, version: string | null) => void;
}) {
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const deployed = config.deployedLoaderVariant;
  const version = config.deployedLoaderVersion;

  const deploy = async (variant: "user" | "dev") => {
    setBusy(variant);
    setError(null);
    try {
      const result = await rpcCall<LoaderDeployResult>("deployLoader", { variant });
      onDeployed(result.variant, result.version ?? null);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(null);
    }
  };

  const buttonLabel = (variant: "user" | "dev") => {
    if (busy === variant) return "Deploying…";
    return deployed === variant ? `Update ${variant}` : `Deploy ${variant}`;
  };

  return (
    <Field label="Loader" hint="`dev` adds the Studio bridge + diagnostic probes">
      <div className={styles.pathRow}>
        <span className={styles.pathValue}>
          {deployed !== null
            ? `${deployed}${version !== null ? ` · v${version}` : ""}`
            : "Not deployed"}
        </span>
        <button
          type="button"
          className={styles.linkButton}
          disabled={busy !== null}
          onClick={() => void deploy("user")}
        >
          {buttonLabel("user")}
        </button>
        <button
          type="button"
          className={styles.linkButton}
          disabled={busy !== null}
          onClick={() => void deploy("dev")}
        >
          {buttonLabel("dev")}
        </button>
      </div>
      {error !== null && (
        <p className={styles.error}>
          <CircleX size={12} /> {error}
        </p>
      )}
    </Field>
  );
}

interface PathRowProps {
  readonly label: string;
  readonly hint: string;
  readonly path: string | null;
  readonly missingIcon: React.ReactNode;
  readonly onChange: () => void;
}

function PathRow({ label, hint, path, missingIcon, onChange }: PathRowProps) {
  return (
    <Field label={label} hint={hint}>
      <div className={styles.pathRow}>
        {path !== null ? (
          <span className={styles.pathValue} title={path}>
            {path}
          </span>
        ) : (
          <span className={styles.pathMissing}>{missingIcon} Not set</span>
        )}
        <button type="button" className={styles.linkButton} onClick={onChange}>
          {path !== null ? "Change" : "Set"}
        </button>
      </div>
    </Field>
  );
}

// --- Game bridge section ---------------------------------------------------

// The bridge lives only in the dev loader, so it is offered only when the dev loader
// is the one deployed; with the user loader (or none) there is nothing to connect to.
function BridgeControls({ loaderVariant }: { readonly loaderVariant: string | null }) {
  const { status, setStatus } = useBridgeStatus();

  const isDev = loaderVariant === "dev";
  const enabled = isDev && (status?.enabled ?? false);
  const connected = isDev && (status?.connected ?? false);

  const handleToggle = async (next: boolean) => {
    try {
      setStatus(await bridgeSetEnabled(next));
    } catch (err) {
      console.error("[Settings] bridgeSetEnabled failed:", err);
    }
  };

  const color = connected ? "#3fb950" : enabled ? "#d29922" : "#6e7681";
  const stateLabel = connected ? "Live" : enabled ? "Waiting for game" : "Off";

  return (
    <Field
      label="Live game bridge"
      hint={
        isDev
          ? "Open a live connection between Studio and the running game."
          : "Deploy the dev loader to use the bridge — the user loader has none."
      }
      labelAfter={
        <span
          role="img"
          aria-label={stateLabel}
          title={stateLabel}
          style={{
            display: "inline-block",
            width: 8,
            height: 8,
            marginLeft: 6,
            borderRadius: "50%",
            backgroundColor: color,
            boxShadow: connected ? `0 0 6px ${color}` : "none",
          }}
        />
      }
    >
      {isDev ? (
        <SegmentedControl<"on" | "off">
          value={enabled ? "on" : "off"}
          onChange={(v) => void handleToggle(v === "on")}
          options={[
            { value: "on", label: "On" },
            { value: "off", label: "Off" },
          ]}
        />
      ) : (
        <span className={styles.muted}>Unavailable</span>
      )}
    </Field>
  );
}
