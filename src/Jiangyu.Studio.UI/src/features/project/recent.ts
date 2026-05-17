/**
 * Recent-project list persisted to localStorage. Kept client-side so that
 * multiple Studio instances on the same machine don't race the host's
 * `GlobalConfig.json`, and so that the user can edit / clear entries from
 * the welcome screen without a host round-trip.
 *
 * Order: most recently opened first. Duplicates are deduplicated on record.
 */
const STORAGE_KEY = "jiangyu:recentProjects";
const MAX_ENTRIES = 10;

export function loadRecentProjects(): readonly string[] {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (raw === null) return [];
    const parsed = JSON.parse(raw) as unknown;
    if (!Array.isArray(parsed)) return [];
    return parsed.filter((p): p is string => typeof p === "string");
  } catch {
    return [];
  }
}

function save(entries: readonly string[]): void {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(entries));
  } catch {
    // Quota exceeded or storage unavailable — drop silently.
  }
}

/** Promote `path` to the front of the list; trims to MAX_ENTRIES. */
export function recordRecentProject(path: string): readonly string[] {
  const current = loadRecentProjects();
  const next = [path, ...current.filter((p) => p !== path)].slice(0, MAX_ENTRIES);
  save(next);
  return next;
}

export function removeRecentProject(path: string): readonly string[] {
  const current = loadRecentProjects();
  const next = current.filter((p) => p !== path);
  if (next.length === current.length) return current;
  save(next);
  return next;
}

export function clearRecentProjects(): void {
  save([]);
}
