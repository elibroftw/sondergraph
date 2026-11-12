// Formatting helpers shared by the views.

/** Platform display names and a stable accent colour per source. */
const PLATFORM_LABELS: Record<string, string> = {
  vcard: 'Contacts',
  facebook: 'Facebook',
  instagram: 'Instagram',
  linkedin: 'LinkedIn',
  discord: 'Discord',
  twitter: 'Twitter',
  signal: 'Signal',
  whatsapp: 'WhatsApp',
  telegram: 'Telegram',
  mastodon: 'Mastodon',
  bluesky: 'Bluesky',
}

export function platformLabel(platform: string): string {
  return PLATFORM_LABELS[platform.toLowerCase()] ?? platform
}

/**
 * Platforms that are a way to reach someone rather than a presence on a social network.
 *
 * A phone book entry has no profile: "Contacts" is where the number came from, not a place
 * the person has a page. Neither does Signal, WhatsApp or SMS - the handle is a phone number
 * and there is no URL to open. Treating these as accounts with a missing profile produced the
 * nonsensical "Contacts - no profile URL", which complained about the absence of a thing that
 * cannot exist.
 *
 * Kept as a list of what is *not* a profile rather than a list of what is, so an unfamiliar
 * social platform still shows its link by default instead of being silently stripped of one.
 */
const NON_PROFILE_PLATFORMS = new Set(['vcard', 'signal', 'whatsapp', 'telegram', 'sms'])

/** Whether a platform can meaningfully have a profile URL at all. */
export function hasProfileConcept(platform: string): boolean {
  return !NON_PROFILE_PLATFORMS.has(platform.toLowerCase())
}

/** Platforms shown as ways to reach someone, rather than as social presences. */
export function isContactPlatform(platform: string): boolean {
  return !hasProfileConcept(platform)
}

/**
 * Hue for a platform chip.
 *
 * Known platforms get a hue close to their brand colour, clamped into the cyan-to-violet band
 * the rest of the palette lives in, so the UI stays coherent instead of turning into a
 * rainbow. Unknown platforms fall back to a hash, which keeps them distinguishable.
 *
 * The first version hashed every name, which gave LinkedIn a red chip and Instagram a muddy
 * purple - recognisable as neither the brand nor the theme.
 */
const PLATFORM_HUES: Record<string, number> = {
  instagram: 320,
  facebook: 220,
  linkedin: 205,
  discord: 245,
  twitter: 200,
  vcard: 168,
  contacts: 168,
  signal: 190,
  mastodon: 262,
  bluesky: 200,
  telegram: 200,
}

export function platformHue(platform: string): number {
  const known = PLATFORM_HUES[platform.toLowerCase()]

  if (known !== undefined) {
    return known
  }

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
  { value: 'extended-family', label: 'Extended family' },
  { value: 'acquaintance', label: 'Acquaintances' },
  { value: 'business', label: 'Business' },
  { value: 'family', label: 'Family' },
  { value: 'unknown', label: 'Unrated' },
] as const

/**
 * Sources offered in the filter dropdown.
 *
 * Derived from the data rather than hard-coded where possible, but the short labels belong
 * here: they are what the user types in a smart query and what the chip shows.
 */
export const SOURCES = [
  { value: 'all', label: 'All sources' },
  { value: 'instagram', label: 'Instagram' },
  { value: 'facebook', label: 'Facebook' },
  { value: 'linkedin', label: 'LinkedIn' },
  { value: 'discord', label: 'Discord' },
  { value: 'twitter', label: 'Twitter' },
  { value: 'vcard', label: 'Contacts' },
  { value: 'signal', label: 'Signal' },
] as const

/**
 * Aliases the smart query accepts, mirrored from the server.
 *
 * The server is authoritative; this copy exists so the dropdown can reflect a query like
 * `platform:ig` by highlighting "Instagram" rather than showing blank because "ig" is not a
 * value the select knows.
 */
const PLATFORM_ALIASES: Record<string, string> = {
  ig: 'instagram',
  insta: 'instagram',
  gram: 'instagram',
  fb: 'facebook',
  meta: 'facebook',
  li: 'linkedin',
  in: 'linkedin',
  discord: 'discord',
  twitter: 'twitter',
  x: 'twitter',
  dc: 'discord',
  vcf: 'vcard',
  contacts: 'vcard',
  google: 'vcard',
  takeout: 'vcard',
  bsky: 'bluesky',
  masto: 'mastodon',
}

/** Resolves a platform alias to the key used by the dropdown. */
export function resolvePlatform(value: string): string {
  const key = value.toLowerCase()
  return PLATFORM_ALIASES[key] ?? key
}

/** Whether a source key is one the dropdown can display. */
export function isKnownSource(platform: string): boolean {
  return SOURCES.some((s) => s.value === platform.toLowerCase())
}

export function tierLabel(tier: string): string {
  const found = TIERS.find((t) => t.value === tier)
  return found ? found.label : tier
}

/**
 * Hue per relationship tier, matching the colours the network graph uses.
 *
 * The tier pill used to be the only chip in a contact row with no colour of its own: dark
 * text on a pale grey background, which read as a disabled control rather than a label. A
 * hue fixes both problems at once - the label becomes legible, and the tiers become
 * distinguishable while scanning a list, which is the reason to show them at all.
 *
 * Reusing the graph's hues means a node's colour and its row's colour are the same thing.
 * Hues are spread across the wheel rather than a single ramp; neighbouring tiers were
 * indistinguishable at chip size when they sat close together on the spectrum.
 */
const TIER_HUES: Record<string, number> = {
  family: 330,
  'extended-family': 25,
  best: 300,
  close: 265,
  friend: 215,
  acquaintance: 155,
  business: 48,
  unknown: 210,
}

/** Hue for a relationship tier, for the coloured pill in a contact row. */
export function tierHue(tier: string): number {
  return TIER_HUES[tier.toLowerCase()] ?? 210
}

/** First letters of a name, for the avatar placeholder. */
/**
 * Turns a slug-shaped id into a readable name: "amjad-halis" becomes "Amjad Halis".
 *
 * Used where an id is displayed directly - a friend whose record has not loaded yet, or one
 * that no longer resolves. The distinction matters because an id is a file name, not something
 * a person should have to read.
 *
 * A name that already has spaces is returned unchanged, so this is safe to apply to display
 * names too: "Abdullah Yildirim" and "McDonald's" are left alone.
 */
export function readableName(value: string): string {
  if (!value || value.includes(' ')) {
    return value
  }

  // Only transform things that look like slugs. A handle such as "abdullah_dabbagh_" keeps
  // its underscores, since those are part of how it was recorded rather than a separator.
  if (!/^[a-z0-9]+(-[a-z0-9]+)*$/i.test(value)) {
    return value
  }

  return value
    .split('-')
    .filter(Boolean)
    .map((word) => word.charAt(0).toUpperCase() + word.slice(1))
    .join(' ')
}

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
