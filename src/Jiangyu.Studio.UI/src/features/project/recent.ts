import { loadJson, saveJson } from "@shared/storage";

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

function isArray(value: unknown): value is unknown[] {
  return Array.isArray(value);
}

export function loadRecentProjects(): readonly string[] {
  // Permissive filter: keep valid string entries and drop the rest. A single
  // bad entry from an older schema or a hand-edit shouldn't wipe the list.
  const parsed = loadJson(STORAGE_KEY, isArray);
  if (parsed === null) return [];
  return parsed.filter((p): p is string => typeof p === "string");
}

function save(entries: readonly string[]): void {
  saveJson(STORAGE_KEY, entries);
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
