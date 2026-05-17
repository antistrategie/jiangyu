import { useCallback, useEffect, useRef } from "react";
import { subscribe } from "@shared/rpc";

interface Ticket {
  readonly paneId: string;
  readonly path: string;
}

// Tracks the (paneId, path) pair that initiated a cross-window tab drag so
// that when the host fires tabMovedOut we know which pane to close the tab
// from. Cleared ONLY by the tabMovedOut subscriber — dragend is synchronous
// and can beat the notification, so clearing on dragend races a successful
// drop and makes the close silently no-op.
export function useCrossWindowTabDrag(
  onCloseTabs: (paneId: string, paths: readonly string[]) => void,
): { readonly handleCrossDragStart: (ticket: Ticket) => void } {
  const ticketRef = useRef<Ticket | null>(null);

  const handleCrossDragStart = useCallback((ticket: Ticket) => {
    ticketRef.current = ticket;
  }, []);

  useEffect(() => {
    return subscribe("tabMovedOut", (params) => {
      const { path } = params as { path: string };
      const ticket = ticketRef.current;
      if (ticket?.path !== path) return;
      ticketRef.current = null;
      onCloseTabs(ticket.paneId, [path]);
    });
  }, [onCloseTabs]);

  return { handleCrossDragStart };
}
