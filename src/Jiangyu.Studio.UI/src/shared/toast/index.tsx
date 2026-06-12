import { create } from "zustand";
import { useShallow } from "zustand/react/shallow";
import { pickSticker } from "@shared/toast/stickers";

export interface ToastAction {
  readonly label: string;
  readonly run: () => void;
}

export interface Toast {
  readonly id: string;
  readonly message: string;
  readonly detail?: string;
  readonly variant: "info" | "success" | "error";
  readonly sticker?: string;
  readonly actions?: readonly ToastAction[];
}

interface ToastStore {
  readonly toasts: readonly Toast[];
  readonly push: (toast: Omit<Toast, "id" | "sticker">) => void;
  readonly dismiss: (id: string) => void;
}

const AUTO_DISMISS_MS = 8_000;
let nextId = 0;

// App-wide toast queue. Moved off React context so non-component code (RPC
// error handlers in rpc.ts, background tasks) can push toasts directly via
// `useToastStore.getState().push({...})` without needing a threaded ref.
export const useToastStore = create<ToastStore>((set, get) => ({
  toasts: [],

  push: (toast) => {
    const id = `toast-${++nextId}`;
    const sticker = pickSticker(toast.variant);
    set((s) => ({ toasts: [...s.toasts, { ...toast, id, sticker }] }));
    setTimeout(() => get().dismiss(id), AUTO_DISMISS_MS);
  },

  dismiss: (id) => {
    set((s) => {
      const next = s.toasts.filter((t) => t.id !== id);
      return next.length === s.toasts.length ? s : { toasts: next };
    });
  },
}));

/**
 * Stable `push` action only. This is what almost every consumer wants:
 * subscribing to the toast list itself re-renders the caller on every toast
 * anywhere in the app, so only the toast container should use `useToast`.
 */
export function useToastPush(): ToastStore["push"] {
  return useToastStore((s) => s.push);
}

// Full queue view for the toast container (the one component that renders
// the list). Everything else should use `useToastPush` or, from
// non-component code, `useToastStore.getState().push({...})`.
interface ToastContextValue {
  readonly toasts: readonly Toast[];
  readonly push: ToastStore["push"];
  readonly dismiss: ToastStore["dismiss"];
}

export function useToast(): ToastContextValue {
  return useToastStore(useShallow((s) => ({ toasts: s.toasts, push: s.push, dismiss: s.dismiss })));
}
