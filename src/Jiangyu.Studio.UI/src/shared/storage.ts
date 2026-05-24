// Wrappers over localStorage that handle the cases every caller has to
// handle: missing key, malformed JSON, validation failure, and the quota /
// private-mode error that throws on write. Route storage access through
// here so callers don't reimplement the same try/catch shape.

/**
 * Read a JSON-serialised value from localStorage and narrow it via
 * `validate`. Returns null on any of: key missing, JSON.parse throwing,
 * `validate` returning false, or the storage call itself throwing (Safari
 * private mode treats localStorage access as a SecurityError). Callers
 * substitute their own default — that decision belongs at the call site,
 * not here.
 */
export function loadJson<T>(key: string, validate: (value: unknown) => value is T): T | null {
  try {
    const raw = localStorage.getItem(key);
    if (raw === null) return null;
    const parsed: unknown = JSON.parse(raw);
    return validate(parsed) ? parsed : null;
  } catch {
    return null;
  }
}

/**
 * Write a JSON-serialised value to localStorage. Swallows storage errors so
 * callers don't have to litter `void`s — write failures are non-fatal (we
 * lose the persistence but the in-memory state stays correct). Use
 * `removeKey` to clear an entry rather than writing `null` or `[]`.
 */
export function saveJson(key: string, value: unknown): void {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch {
    // Quota exceeded or storage unavailable — drop silently.
  }
}

/**
 * Read and JSON.parse the value at `key`, returning `unknown` so the caller
 * can clamp/coerce on its own. Returns null when the key is missing, the
 * stored JSON is malformed, or storage is unavailable. Prefer `loadJson`
 * when a structural guard fits; reach for `loadRaw` when the caller already
 * owns a tolerant parser (e.g. settings clamps and falls back per-field).
 */
export function loadRaw(key: string): unknown {
  try {
    const raw = localStorage.getItem(key);
    if (raw === null) return null;
    return JSON.parse(raw);
  } catch {
    return null;
  }
}

/** Delete a key, swallowing storage errors. */
export function removeKey(key: string): void {
  try {
    localStorage.removeItem(key);
  } catch {
    // Storage unavailable — drop silently.
  }
}
