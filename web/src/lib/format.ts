// Formatting helpers shared by the views.

/** Platform display names and a stable accent colour per source. */
const PLATFORM_LABELS: Record<string, string> = {
  vcard: 'Contacts',
  facebook: 'Facebook',
  instagram: 'Instagram',
  linkedin: 'LinkedIn',
  mastodon: 'Mastodon',
  bluesky: 'Bluesky',
}

export function platformLabel(platform: string): string {
  return PLATFORM_LABELS[platform.toLowerCase()] ?? platform
}

/**
 * A deterministic colour for a platform, derived from its name.
 *
 * Deterministic rather than a lookup table so a new platform gets a sensible colour without
 * anyone editing this file — and the same platform is always the same colour across views.
 */
export function platformHue(platform: string): number {
  let hash = 0

  for (let i = 0; i < platform.length; i++) {
    hash = (hash * 31 + platform.charCodeAt(i)) % 360
  }

  return hash
}

/** Tier labels and their ordering, matching the server's ContactTier values. */
export const TIERS = [
  { value: 'all', label: 'Everyone' },
  { value: 'best', label: 'Best friends' },
  { value: 'close', label: 'Close friends' },
  { value: 'friend', label: 'Friends' },
  { value: 'acquaintance', label: 'Acquaintances' },
  { value: 'family', label: 'Family' },
  { value: 'unknown', label: 'Unrated' },
] as const

export function tierLabel(tier: string): string {
  const found = TIERS.find((t) => t.value === tier)
  return found ? found.label : tier
}

/** First letters of a name, for the avatar placeholder. */
export function initials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean)

  if (parts.length === 0) {
    return '?'
  }

  if (parts.length === 1) {
    // Single-word names (and emoji) have no useful "initials"; take the first two runes.
    // Spreading is required: indexing a string splits a surrogate pair in half.
    return [...parts[0]].slice(0, 2).join('')
  }

  const first = [...parts[0]][0] ?? ''
  const last = [...parts[parts.length - 1]][0] ?? ''

  return first + last
}

/** Groups a count into a readable string. */
export function people(count: number): string {
  return count === 1 ? '1 person' : `${count.toLocaleString()} people`
}
