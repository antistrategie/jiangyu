/**
 * Jiangyu character sticker pools, grouped by mood.
 *
 * 001 = punching, 002 = waving goodbye, 003 = winding up attack,
 * 004 = triumphant flex, 005 = "come at me", 006 = sad/rain,
 * 007 = happy/hearts, 008 = asking for a fight, 009 = double pointing/agreement.
 */

export type StickerMood = "success" | "error" | "info";

export const STICKER_POOLS: Record<StickerMood, readonly string[]> = {
  success: ["/stickers/Jiangyu_004.jpg", "/stickers/Jiangyu_007.jpg", "/stickers/Jiangyu_009.jpg"],
  error: [
    "/stickers/Jiangyu_001.jpg",
    "/stickers/Jiangyu_003.jpg",
    "/stickers/Jiangyu_006.jpg",
    "/stickers/Jiangyu_008.jpg",
  ],
  info: ["/stickers/Jiangyu_002.jpg", "/stickers/Jiangyu_005.jpg"],
};

export const ALL_STICKERS: readonly string[] = Object.values(STICKER_POOLS).flat();

/** Pick a random sticker for the given mood. Pools are non-empty by construction. */
export function pickSticker(mood: StickerMood): string {
  const pool = STICKER_POOLS[mood];
  const picked = pool[Math.floor(Math.random() * pool.length)];
  if (picked === undefined) throw new Error(`Empty sticker pool for mood "${mood}"`);
  return picked;
}
