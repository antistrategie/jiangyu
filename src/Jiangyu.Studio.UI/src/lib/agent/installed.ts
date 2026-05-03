/**
 * Installed-agent persistence. The list lives in `StudioSettings.installedAgents`
 * (host-side `studio.json`); the frontend mirrors it in localStorage for an
 * instant first paint, same pattern as the rest of `lib/settings.ts`.
 *
 * Order matters: index 0 is the agent that boots when the user lands on an
 * empty agent panel. A future built-in Jiangyu agent will be inserted at the
 * head and become the implicit default — no separate "default" flag needed.
 */
import { create } from "zustand";
import { rpcCall, type InstalledAgent, type StudioSettings } from "@lib/rpc";

const STORAGE_KEY = "jiangyu:setting:installedAgents";

function loadInitial(): InstalledAgent[] {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (raw === null) return [];
    const parsed: unknown = JSON.parse(raw);
    return Array.isArray(parsed) ? (parsed as InstalledAgent[]) : [];
  } catch {
    return [];
  }
}

function saveLocal(list: readonly InstalledAgent[]): void {
  localStorage.setItem(STORAGE_KEY, JSON.stringify(list));
}

interface InstalledAgentsState {
  readonly agents: InstalledAgent[];
  readonly setAgents: (agents: readonly InstalledAgent[]) => void;
  readonly install: (agent: InstalledAgent) => Promise<void>;
  readonly uninstall: (id: string) => Promise<void>;
}

export const useInstalledAgents = create<InstalledAgentsState>((set, get) => ({
  agents: loadInitial(),

  setAgents: (agents) => {
    saveLocal(agents);
    set({ agents: [...agents] });
  },

  install: async (agent) => {
    const current = get().agents;
    // Re-installing replaces the existing entry to pick up version changes
    // without losing list position.
    const idx = current.findIndex((a) => a.id === agent.id);
    const next = idx >= 0 ? current.with(idx, agent) : [...current, agent];
    saveLocal(next);
    set({ agents: next });
    await persist(next);
  },

  uninstall: async (id) => {
    const next = get().agents.filter((a) => a.id !== id);
    saveLocal(next);
    set({ agents: next });
    await persist(next);
  },
}));

async function persist(list: readonly InstalledAgent[]): Promise<void> {
  try {
    // Fire-and-forget the host write. We deliberately ignore the response.
    //
    // The host echoes the full settings object after applying the change,
    // and an earlier version of this code applied that echo back into the
    // store. That created a flicker on rapid successive installs: the
    // first response would land while the second install had already
    // optimistically pushed [a, b], rolling the visible list back to [a]
    // before the second response arrived. The host doesn't normalise list
    // values, so the echo carried no information the local state didn't
    // already have. Just persist and move on.
    await rpcCall("setStudioSetting", { key: "installedAgents", value: list });
  } catch {
    // Host not available — the localStorage write is sufficient for this session.
  }
}

/**
 * Reconcile localStorage with the host's authoritative `studio.json` once
 * at startup. Mirrors `initSettings()` in `lib/settings.ts`.
 */
export function initInstalledAgents(): void {
  rpcCall<StudioSettings>("getStudioSettings")
    .then((settings) => {
      const list = settings.installedAgents ?? [];
      saveLocal(list);
      useInstalledAgents.setState({ agents: [...list] });
    })
    .catch(() => {
      // Host not available — rely on localStorage.
    });
}
