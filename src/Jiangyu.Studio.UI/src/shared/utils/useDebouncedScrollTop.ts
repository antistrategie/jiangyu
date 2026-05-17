import { useCallback, useEffect, useRef, useState } from "react";

// Commit-on-idle scroll tracking. The raw `scrollTop` value is kept in a ref
// so 60fps scroll events don't cascade through parent state, and only the
// final resting position is written to React state after `delayMs` of
// inactivity. The committed value is what the browsers persist back through
// onStateChange; the ref isn't observable.
export function useDebouncedScrollTop(
  initial: number,
  delayMs = 200,
): readonly [number, (e: React.UIEvent<HTMLElement>) => void] {
  const [value, setValue] = useState(initial);
  const timerRef = useRef<number | null>(null);
  const pendingRef = useRef<number | null>(null);

  const onScroll = useCallback(
    (e: React.UIEvent<HTMLElement>) => {
      pendingRef.current = e.currentTarget.scrollTop;
      if (timerRef.current !== null) return;
      timerRef.current = window.setTimeout(() => {
        if (pendingRef.current !== null) setValue(pendingRef.current);
        timerRef.current = null;
      }, delayMs);
    },
    [delayMs],
  );

  useEffect(() => {
    return () => {
      if (timerRef.current !== null) window.clearTimeout(timerRef.current);
    };
  }, []);

  return [value, onScroll] as const;
}
