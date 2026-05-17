/**
 * Tiny store for the agent registry modal's open state. Lifted out of
 * AgentPanel so it can be opened from anywhere — palette action, future
 * settings entry, etc. — and so multiple AgentPanels in different panes
 * share a single modal instance instead of stacking duplicates.
 */
import { create } from "zustand";

interface RegistryModalStore {
  readonly open: boolean;
  readonly setOpen: (open: boolean) => void;
}

export const useRegistryModalStore = create<RegistryModalStore>((set) => ({
  open: false,
  setOpen: (open) => set({ open }),
}));
