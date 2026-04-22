import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from "react";
import { pickSticker } from "./stickers.ts";

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

interface ToastContextValue {
  readonly toasts: readonly Toast[];
  readonly push: (toast: Omit<Toast, "id" | "sticker">) => void;
  readonly dismiss: (id: string) => void;
}

const ToastContext = createContext<ToastContextValue | null>(null);

let nextId = 0;

const AUTO_DISMISS_MS = 8_000;

export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<readonly Toast[]>([]);

  const dismiss = useCallback((id: string) => {
    setToasts((prev) => {
      const next = prev.filter((t) => t.id !== id);
      return next.length === prev.length ? prev : next;
    });
  }, []);

  const push = useCallback(
    (toast: Omit<Toast, "id" | "sticker">) => {
      const id = `toast-${++nextId}`;
      const sticker = pickSticker(toast.variant);
      setToasts((prev) => [...prev, { ...toast, id, sticker }]);
      setTimeout(() => dismiss(id), AUTO_DISMISS_MS);
    },
    [dismiss],
  );

  const value = useMemo<ToastContextValue>(() => ({ toasts, push, dismiss }), [toasts, push, dismiss]);

  return <ToastContext value={value}>{children}</ToastContext>;
}

export function useToast(): ToastContextValue {
  const ctx = useContext(ToastContext);
  if (ctx === null) throw new Error("useToast must be used inside <ToastProvider>");
  return ctx;
}
