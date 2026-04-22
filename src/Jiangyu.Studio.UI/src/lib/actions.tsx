import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useId,
  useMemo,
  useState,
  type ReactNode,
} from "react";

export const PALETTE_SCOPE = {
  App: "APP · 应用",
  Project: "PROJECT · 项目",
  View: "VIEW · 视图",
  File: "FILE · 文件",
  Editor: "EDITOR · 编辑",
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

interface RegistryValue {
  readonly slots: ReadonlyMap<string, readonly PaletteAction[]>;
  readonly register: (slot: string, actions: readonly PaletteAction[]) => void;
  readonly unregister: (slot: string) => void;
}

const ActionRegistryContext = createContext<RegistryValue | null>(null);

export function ActionRegistryProvider({ children }: { children: ReactNode }) {
  const [slots, setSlots] = useState<ReadonlyMap<string, readonly PaletteAction[]>>(new Map());

  const register = useCallback((slot: string, actions: readonly PaletteAction[]) => {
    setSlots((prev) => {
      if (prev.get(slot) === actions) return prev;
      const next = new Map(prev);
      next.set(slot, actions);
      return next;
    });
  }, []);

  const unregister = useCallback((slot: string) => {
    setSlots((prev) => {
      if (!prev.has(slot)) return prev;
      const next = new Map(prev);
      next.delete(slot);
      return next;
    });
  }, []);

  const value = useMemo<RegistryValue>(
    () => ({ slots, register, unregister }),
    [slots, register, unregister],
  );

  return <ActionRegistryContext.Provider value={value}>{children}</ActionRegistryContext.Provider>;
}

/**
 * Register a list of palette actions. The caller should memoise the array so
 * actions only re-register when their identity or contents change.
 */
export function useRegisterActions(actions: readonly PaletteAction[]): void {
  const ctx = useContext(ActionRegistryContext);
  // Pull the callbacks out so the effect depends on stable references rather
  // than the context object itself — otherwise every register() triggers a new
  // ctx (via slots → useMemo) and cascades into re-registering all slots.
  const register = ctx?.register;
  const unregister = ctx?.unregister;
  const slot = useId();

  useEffect(() => {
    if (!register || !unregister) return;
    register(slot, actions);
    return () => unregister(slot);
  }, [register, unregister, slot, actions]);
}

/** Snapshot of all currently-registered actions, ordered by registration time. */
export function useRegisteredActions(): readonly PaletteAction[] {
  const ctx = useContext(ActionRegistryContext);
  const slots = ctx?.slots;
  return useMemo(() => {
    if (!slots) return [];
    const out: PaletteAction[] = [];
    for (const actions of slots.values()) out.push(...actions);
    return out;
  }, [slots]);
}
