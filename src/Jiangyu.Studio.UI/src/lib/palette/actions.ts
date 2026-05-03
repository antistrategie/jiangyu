import { useEffect, useId, useMemo } from "react";
import { create } from "zustand";
import { useShallow } from "zustand/react/shallow";

export const PALETTE_SCOPE = {
  App: "APP · 应用",
  Project: "PROJECT · 项目",
  View: "VIEW · 视图",
  File: "FILE · 文件",
  Editor: "EDITOR · 编辑",
  AI: "AI · 智能",
  GoToFile: "GO TO FILE · 跳转",
} as const;

export interface PaletteAction {
  readonly id: string;
  readonly label: string;
  readonly scope: string;
  readonly cn?: string;
  readonly kbd?: string;
  readonly desc?: string;
  readonly run: () => void | Promise<void>;
}

interface PaletteStore {
  /** slotId → the list of actions that slot last registered. */
  readonly slots: Readonly<Record<string, readonly PaletteAction[]>>;

  readonly register: (slot: string, actions: readonly PaletteAction[]) => void;
  readonly unregister: (slot: string) => void;
}

// Aggregates palette actions from every registration slot across the tree.
// Replaces the earlier <ActionRegistryProvider> context — moving to zustand
// lets non-component code (e.g. host notification handlers) register actions
// without needing to be inside the React tree.
export const usePaletteStore = create<PaletteStore>((set) => ({
  slots: {},

  register: (slot, actions) => {
    set((s) => (s.slots[slot] === actions ? s : { slots: { ...s.slots, [slot]: actions } }));
  },

  unregister: (slot) => {
    set((s) => {
      if (!(slot in s.slots)) return s;
      const { [slot]: _removed, ...rest } = s.slots;
      void _removed;
      return { slots: rest };
    });
  },
}));

/**
 * Register a list of palette actions. The caller should memoise the array so
 * actions only re-register when their identity or contents change.
 */
export function useRegisterActions(actions: readonly PaletteAction[]): void {
  const slotId = useId();
  useEffect(() => {
    const { register, unregister } = usePaletteStore.getState();
    register(slotId, actions);
    return () => unregister(slotId);
  }, [slotId, actions]);
}

/** Snapshot of all currently-registered actions, ordered by registration time. */
export function useRegisteredActions(): readonly PaletteAction[] {
  // useShallow makes the selector return a stable array ref while the slot
  // contents are unchanged — otherwise every registration anywhere would
  // re-render every consumer.
  const slots = usePaletteStore(useShallow((s) => s.slots));
  return useMemo(() => {
    const out: PaletteAction[] = [];
    for (const actions of Object.values(slots)) out.push(...actions);
    return out;
  }, [slots]);
}
