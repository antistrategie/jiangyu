import { useCallback, useEffect, useMemo, useState } from "react";
import { Modal } from "@components/Modal/Modal";
import {
  fetchRegistry,
  toInstalledAgent,
  distributionKindsFor,
  preferredDistribution,
  type RegistryAgent,
  type RegistryDocument,
} from "@lib/agent/registry";
import { useInstalledAgents } from "@lib/agent/installed";
import styles from "./AgentRegistryModal.module.css";

interface AgentRegistryModalProps {
  readonly onClose: () => void;
}

export function AgentRegistryModal({ onClose }: AgentRegistryModalProps) {
  const [registry, setRegistry] = useState<RegistryDocument | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [query, setQuery] = useState("");

  const installed = useInstalledAgents((s) => s.agents);
  const install = useInstalledAgents((s) => s.install);
  const uninstall = useInstalledAgents((s) => s.uninstall);

  // Ref callback that focuses the search field once on mount, replacing
  // the autoFocus prop (a11y rule: prefer programmatic focus tied to a
  // user-initiated open, since modals are opened by user action).
  const focusOnMount = useCallback((el: HTMLInputElement | null) => {
    el?.focus();
  }, []);

  useEffect(() => {
    let cancelled = false;
    fetchRegistry()
      .then((doc) => {
        if (!cancelled) setRegistry(doc);
      })
      .catch((err: unknown) => {
        if (!cancelled) setError(err instanceof Error ? err.message : String(err));
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const filtered = useMemo(() => {
    if (registry === null) return [];
    const trimmed = query.trim().toLowerCase();
    if (trimmed === "") return registry.agents;
    return registry.agents.filter(
      (a) =>
        a.name.toLowerCase().includes(trimmed) || a.description.toLowerCase().includes(trimmed),
    );
  }, [registry, query]);

  return (
    <Modal onClose={onClose} ariaLabelledBy="agent-registry-title">
      <div className={styles.dialog}>
        <header className={styles.header}>
          <h2 id="agent-registry-title" className={styles.title}>
            Agent Registry · 代理库
          </h2>
          <button type="button" className={styles.close} aria-label="Close" onClick={onClose}>
            ×
          </button>
        </header>

        <div className={styles.searchRow}>
          <input
            ref={focusOnMount}
            type="text"
            className={styles.searchInput}
            aria-label="Filter agents"
            placeholder="Filter agents…"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
          />
        </div>

        <div className={styles.body}>
          {error !== null && (
            <div className={styles.errorState}>Failed to load registry: {error}</div>
          )}
          {error === null && registry === null && (
            <div className={styles.loadingState}>Loading…</div>
          )}
          {registry !== null && filtered.length === 0 && (
            <div className={styles.emptyState}>No agents match.</div>
          )}
          {registry !== null && filtered.length > 0 && (
            <ul className={styles.list}>
              {filtered.map((agent) => (
                <AgentRow
                  key={agent.id}
                  agent={agent}
                  installed={installed.some((a) => a.id === agent.id)}
                  onInstall={() => {
                    const record = toInstalledAgent(agent);
                    if (record !== null) void install(record);
                  }}
                  onUninstall={() => {
                    void uninstall(agent.id);
                  }}
                />
              ))}
            </ul>
          )}
        </div>
      </div>
    </Modal>
  );
}

interface AgentRowProps {
  readonly agent: RegistryAgent;
  readonly installed: boolean;
  readonly onInstall: () => void;
  readonly onUninstall: () => void;
}

function AgentRow({ agent, installed, onInstall, onUninstall }: AgentRowProps) {
  const kinds = distributionKindsFor(agent);
  const supported = preferredDistribution(agent);

  return (
    <li className={styles.row}>
      {agent.icon !== undefined ? (
        <img src={agent.icon} alt="" className={styles.icon} />
      ) : (
        <div className={styles.iconPlaceholder} aria-hidden="true" />
      )}
      <div className={styles.info}>
        <div className={styles.nameRow}>
          <span className={styles.name}>{agent.name}</span>
          <span className={styles.version}>v{agent.version}</span>
        </div>
        <div className={styles.description}>{agent.description}</div>
        <div className={styles.badges}>
          {kinds.map((kind) => (
            <span
              key={kind}
              className={`${styles.badge} ${kind === supported ? styles.badge_supported : ""}`}
              title={kind === supported ? "Supported" : "Not yet supported"}
            >
              {kind}
            </span>
          ))}
        </div>
      </div>
      <div className={styles.action}>
        {installed ? (
          <button type="button" className={styles.removeBtn} onClick={onUninstall}>
            Remove
          </button>
        ) : supported !== null ? (
          <button type="button" className={styles.installBtn} onClick={onInstall}>
            Install
          </button>
        ) : (
          <span className={styles.unsupportedNote}>Unsupported</span>
        )}
      </div>
    </li>
  );
}
