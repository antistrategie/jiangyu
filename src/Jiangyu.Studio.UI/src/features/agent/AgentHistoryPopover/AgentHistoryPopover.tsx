import { useEffect, useState } from "react";
import {
  agentSessionDelete,
  agentSessionResume,
  agentSessionsList,
  agentStart,
} from "@features/agent/rpc";
import { useAgentStore } from "@features/agent/store";
import { useInstalledAgents } from "@features/agent/installed";
import {
  MenuList,
  MenuListBody,
  MenuItem,
  MenuItemContent,
  MenuItemLabel,
  MenuItemSubtext,
} from "@shared/ui/MenuList/MenuList";
import type { AgentSessionMeta } from "@shared/rpc";
import styles from "./AgentHistoryPopover.module.css";

interface AgentHistoryPopoverProps {
  /** Called when the popover wants to close itself (resume picked, or
   *  outside-click / Escape from the parent). Omit for inline use. */
  readonly onClose?: () => void;
  /** Render flush in the surrounding flow (used by the empty state)
   *  instead of as an absolutely-positioned popover under the trigger. */
  readonly inline?: boolean;
  /** Cap on visible sessions; the rest are dropped from this view (still
   *  available via the popover). Used by the empty state to keep the
   *  picker card compact. */
  readonly limit?: number;
  /** Inline variant only. Rendered above the list when at least one
   *  session exists; hidden when the list would be empty so the empty
   *  state doesn't show a stray heading. */
  readonly inlineHeader?: string;
}

/**
 * History list. Default mode is a popover anchored under the clock-icon
 * header button (caller renders behind a conditional and supplies an
 * outer positioning wrapper). Inline mode skips the absolute positioning
 * so the empty state can flow it inside its picker card.
 */
export function AgentHistoryPopover({
  onClose,
  inline = false,
  limit,
  inlineHeader,
}: AgentHistoryPopoverProps) {
  const [sessions, setSessions] = useState<readonly AgentSessionMeta[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    agentSessionsList()
      .then((file) => {
        if (cancelled) return;
        // Most-recent first.
        const list = [...(file.sessions ?? [])].sort((a, b) => b.updatedAt - a.updatedAt);
        setSessions(list);
        setLoading(false);
      })
      .catch((err: unknown) => {
        if (cancelled) return;
        setError(err instanceof Error ? err.message : String(err));
        setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const handleResume = (session: AgentSessionMeta) => {
    onClose?.();

    const state = useAgentStore.getState();
    const installed = useInstalledAgents.getState().agents;

    // Same agent already running → resume directly. The current code path
    // (beginReplay + agentSessionResume) works without restarting the
    // subprocess.
    if (
      session.agentId !== null &&
      session.agentId !== undefined &&
      session.agentId === state.currentAgentId &&
      state.connected
    ) {
      state.beginReplay(session.id);
      void agentSessionResume(session.id).catch((err: unknown) => {
        useAgentStore.getState().handleResumeResult({
          error: err instanceof Error ? err.message : String(err),
        });
      });
      return;
    }

    // Otherwise we need to (re)boot the right agent first. Look it up in
    // the installed list by id; if missing, surface an error and bail.
    const targetAgent =
      session.agentId !== null && session.agentId !== undefined
        ? installed.find((a) => a.id === session.agentId)
        : null;

    if (targetAgent === null || targetAgent === undefined) {
      useAgentStore.setState({
        connectError:
          session.agentId !== null && session.agentId !== undefined
            ? `The agent "${session.agentId}" for this session is no longer installed.`
            : "This session has no recorded agent and can't be resumed.",
      });
      return;
    }

    // Queue the resume as a start-time intent. setConnecting clears all
    // session state and flips pendingSession to {kind:"resume",…}; the
    // auto-effect in AgentPanel picks that up once handleStartResult
    // flips connected to true.
    state.setConnecting(targetAgent.id, { kind: "resume", sessionId: session.id });
    void agentStart(targetAgent.command, targetAgent.args ?? [], targetAgent.id).catch(
      (err: unknown) => {
        useAgentStore.getState().handleStartResult({
          error: err instanceof Error ? err.message : String(err),
        });
      },
    );
  };

  const handleDelete = (id: string) => {
    void agentSessionDelete(id).then((file) => {
      const list = [...(file.sessions ?? [])].sort((a, b) => b.updatedAt - a.updatedAt);
      setSessions(list);
    });
  };

  // The inline variant is used by the empty state where the picker card
  // already has the agent menu above us. If there are no sessions, just
  // render nothing — including the optional header — so we don't leave a
  // stray "Or resume" heading floating above empty space.
  if (inline && !loading && error === null && sessions.length === 0) {
    return null;
  }

  const visible = limit !== undefined ? sessions.slice(0, limit) : sessions;

  const list = (
    <MenuList className={inline ? styles.inline : styles.popover}>
      {loading && <div className={styles.placeholder}>Loading…</div>}
      {error !== null && <div className={styles.errorState}>{error}</div>}
      {!loading && error === null && sessions.length === 0 && (
        <div className={styles.placeholder}>No previous sessions.</div>
      )}
      {visible.length > 0 && (
        <MenuListBody>
          {visible.map((session) => (
            <SessionRow
              key={session.id}
              session={session}
              onResume={() => handleResume(session)}
              onDelete={() => handleDelete(session.id)}
            />
          ))}
        </MenuListBody>
      )}
    </MenuList>
  );

  if (inline && inlineHeader !== undefined) {
    return (
      <>
        <h3 className={styles.inlineHeader}>{inlineHeader}</h3>
        {list}
      </>
    );
  }

  return list;
}

interface SessionRowProps {
  readonly session: AgentSessionMeta;
  readonly onResume: () => void;
  readonly onDelete: () => void;
}

function SessionRow({ session, onResume, onDelete }: SessionRowProps) {
  // Prefer the InstalledAgent fields (friendly display name + icon) over
  // the agent's self-reported name stored in metadata. The stored name
  // comes from initialize.AgentInfo.Name and is often a package id like
  // "@agentclientprotocol/claude-agent-acp"; the InstalledAgent name is
  // what the modder picked from the registry ("Claude Agent"). Sessions
  // pre-dating the agentId rollout fall back to the stored name.
  const installedAgents = useInstalledAgents((s) => s.agents);
  const installedAgent =
    session.agentId !== null && session.agentId !== undefined
      ? installedAgents.find((a) => a.id === session.agentId)
      : undefined;

  const label = session.title ?? session.firstMessage ?? "(untitled)";
  const meta = relativeTime(session.updatedAt);
  const displayName = installedAgent?.name ?? session.agentName ?? null;
  const iconUrl = installedAgent?.iconUrl ?? null;

  return (
    <div className={styles.row}>
      <MenuItem onClick={onResume}>
        <MenuItemContent>
          <MenuItemLabel>{label}</MenuItemLabel>
          <MenuItemSubtext>
            <span className={styles.rowItem}>
              {iconUrl !== null ? (
                <img src={iconUrl} alt="" className={styles.rowIcon} />
              ) : (
                <span className={styles.rowIconPlaceholder} aria-hidden="true" />
              )}
              <span>{displayName !== null ? `${displayName} · ${meta}` : meta}</span>
            </span>
          </MenuItemSubtext>
        </MenuItemContent>
      </MenuItem>
      <button
        type="button"
        className={styles.deleteBtn}
        aria-label="Forget this session"
        title="Forget this session"
        onClick={(e) => {
          // Don't propagate to the resume row.
          e.stopPropagation();
          onDelete();
        }}
      >
        ×
      </button>
    </div>
  );
}

function relativeTime(updatedAtMs: number): string {
  const diffMs = Date.now() - updatedAtMs;
  const diffSec = Math.round(diffMs / 1000);
  if (diffSec < 60) return "just now";
  const diffMin = Math.round(diffSec / 60);
  if (diffMin < 60) return `${diffMin}m ago`;
  const diffHr = Math.round(diffMin / 60);
  if (diffHr < 24) return `${diffHr}h ago`;
  const diffDay = Math.round(diffHr / 24);
  if (diffDay < 7) return `${diffDay}d ago`;
  return new Date(updatedAtMs).toLocaleDateString();
}
