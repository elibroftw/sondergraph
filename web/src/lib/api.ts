// Typed client for the sondergraph API.
//
// All requests are relative, so the same code works when the UI is served by the API (the
// normal case) and when it is proxied during development. No base URL, no CORS.

/** How a person is summarised in the list view. */
export interface AccountSummary {
  platform: string
  handle: string
  displayName: string | null
  profileUrl: string | null
}

export interface LocationSummary {
  place: string
  kind: string
  note: string | null
}

export interface PersonSummary {
  id: string
  name: string
  tier: string
  tierLabel: string
  preferred: string | null
  preferredLabel: string
  howWeMet: string | null
  howWeMetLabel: string
  gender: string | null
  phones: string[]
  emails: string[]
  locations: LocationSummary[]
  accounts: AccountSummary[]
  sources: string[]
  tags: string[]
  noteCount: number
}

export interface PersonDetail extends PersonSummary {
  aliases: string[]
  notes: { fileName: string; body: string }[]
}

export interface Stats {
  people: number
  withPhone: number
  withEmail: number
  withLocation: number
  withNotes: number
  multiSource: number
  platforms: Record<string, number>
  sources: Record<string, number>
  tiers: Record<string, number>
}

export interface WorkspaceState {
  ok: boolean
  root: string | null
  data: string | null
  error: string | null
  hint: string | null
}

export interface Health {
  status: string
  version: string
  protocol: number
  workspace: WorkspaceState
}

export interface NoteResponse {
  personId: string
  body: string
}

export interface LinkCandidate {
  left: { id: string; name: string; sources: string[] }
  right: { id: string; name: string; sources: string[] }
  score: number
  evidence: { kind: string; detail: string; confidence: number }[]
  combined: string
}

export interface Channel {
  value: string
  label: string
  count: number
}

export interface ChannelsResponse {
  channels: string[]
  suggested: boolean
  inUse: Channel[]
}

export interface DuplicateGroup {
  name: string
  people: PersonSummary[]
}

export interface PersonPage {
  total: number
  returned: number
  people: PersonSummary[]
}

/** An error carrying the server's structured code, not just a message. */
export class ApiError extends Error {
  constructor(
    message: string,
    readonly code: string,
    readonly hint: string | null,
    readonly status: number,
  ) {
    super(message)
    this.name = 'ApiError'
  }
}

/**
 * Sends a PATCH and returns the parsed body.
 *
 * Shared so the error shape is decoded once: every mutation needs the same treatment, and
 * duplicating it invites one endpoint silently reporting a bare status instead of the
 * server's structured message.
 */
async function patch<T>(path: string, body: unknown): Promise<T> {
  const response = await fetch(path, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })

  if (!response.ok) {
    let detail: { error?: string; hint?: string; code?: string } = {}
    try {
      detail = await response.json()
    } catch {
      // Non-JSON error; the status text is all we have.
    }
    throw new ApiError(
      detail.error ?? response.statusText ?? 'Request failed',
      detail.code ?? 'Unknown',
      detail.hint ?? null,
      response.status,
    )
  }

  return (await response.json()) as T
}

/** POSTs JSON and returns the parsed body. */
async function post<T>(path: string, body: unknown): Promise<T> {
  const response = await fetch(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })

  if (!response.ok) {
    throw await toError(response)
  }

  return (await response.json()) as T
}

/** DELETEs and parses a JSON body, for endpoints that return the updated state. */
async function del2<T>(path: string): Promise<T> {
  const response = await fetch(path, { method: 'DELETE' })

  if (!response.ok) {
    throw await toError(response)
  }

  return (await response.json()) as T
}

/** DELETEs and returns nothing. */
async function del(path: string): Promise<void> {
  const response = await fetch(path, { method: 'DELETE' })

  if (!response.ok) {
    throw await toError(response)
  }
}

/**
 * Decodes the server's structured error shape.
 *
 * One helper so every mutation reports the same way; duplicating it invited one endpoint to
 * surface a bare status instead of the server's message and hint.
 */
async function toError(response: Response): Promise<ApiError> {
  let detail: { error?: string; hint?: string; code?: string } = {}

  try {
    detail = await response.json()
  } catch {
    // Non-JSON; the status text is all we have.
  }

  return new ApiError(
    detail.error ?? response.statusText ?? 'Request failed',
    detail.code ?? 'Unknown',
    detail.hint ?? null,
    response.status,
  )
}

async function request<T>(path: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(path, { signal })

  if (!response.ok) {
    // The API returns { error, hint, code } for failures. Falling back to the status text
    // keeps the UI from showing "undefined" when something upstream returns plain HTML.
    let body: { error?: string; hint?: string; code?: string } = {}

    try {
      body = await response.json()
    } catch {
      // Not JSON; the status text is all we have.
    }

    throw new ApiError(
      body.error ?? response.statusText ?? 'Request failed',
      body.code ?? 'Unknown',
      body.hint ?? null,
      response.status,
    )
  }

  return (await response.json()) as T
}

export const api = {
  health: (signal?: AbortSignal) => request<Health>('/api/health', signal),

  stats: (signal?: AbortSignal) => request<Stats>('/api/stats', signal),

  people: (
    query: string,
    tier: string,
    platform = 'all',
    place = 'all',
    signal?: AbortSignal,
  ) => {
    const params = new URLSearchParams()

    if (query.trim()) {
      params.set('q', query.trim())
    }

    if (tier && tier !== 'all') {
      params.set('tier', tier)
    }

    if (platform && platform !== 'all') {
      params.set('platform', platform)
    }

    if (place && place !== 'all') {
      params.set('place', place)
    }

    const suffix = params.toString()
    return request<PersonPage>(`/api/people${suffix ? `?${suffix}` : ''}`, signal)
  },

  person: (id: string, signal?: AbortSignal) =>
    request<PersonDetail>(`/api/people/${encodeURIComponent(id)}`, signal),

  duplicates: (signal?: AbortSignal) =>
    request<{ groups: number; duplicates: DuplicateGroup[] }>('/api/duplicates', signal),

  getNote: (id: string, signal?: AbortSignal) =>
    request<NoteResponse>(`/api/people/${encodeURIComponent(id)}/note`, signal),

  /** Saves the note. An empty body deletes it, matching the CLI and the server. */
  saveNote: async (id: string, body: string): Promise<void> => {
    const response = await fetch(`/api/people/${encodeURIComponent(id)}/note`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ body }),
    })

    if (!response.ok) {
      let detail: { error?: string; hint?: string; code?: string } = {}
      try {
        detail = await response.json()
      } catch {
        // Non-JSON error; fall through to the status text.
      }
      throw new ApiError(
        detail.error ?? response.statusText,
        detail.code ?? 'Unknown',
        detail.hint ?? null,
        response.status,
      )
    }
  },

  setTier: async (id: string, tier: string): Promise<void> => {
    await patch(`/api/people/${encodeURIComponent(id)}/tier`, { tier })
  },

  changeTags: async (id: string, add: string[], remove: string[]): Promise<string[]> => {
    const result = await patch<{ tags: string[] }>(
      `/api/people/${encodeURIComponent(id)}/tags`,
      { add, remove },
    )
    return result.tags
  },

  setLocation: async (
    id: string,
    place: string,
    kind: string,
    remove = false,
  ): Promise<LocationSummary[]> => {
    const result = await patch<{ locations: LocationSummary[] }>(
      `/api/people/${encodeURIComponent(id)}/locations`,
      { place, kind, remove },
    )
    return result.locations
  },

  /**
   * Channels available for the dropdown.
   *
   * Derived server-side from the people who use them, so the list cannot hold a leftover.
   * When nobody has set one, the server returns sensible suggestions flagged as such.
   */
  channels: (signal?: AbortSignal) => request<ChannelsResponse>('/api/channels', signal),

  /** Cross-platform pairs worth linking, strongest evidence first. */
  links: (minScore = 0.5, signal?: AbortSignal) =>
    request<{ total: number; candidates: LinkCandidate[] }>(
      `/api/links?minScore=${minScore}`,
      signal,
    ),

  /** Merge suggestions for one contact. Cheap, but still debounced by the caller. */
  suggestions: (id: string, signal?: AbortSignal) =>
    request<{
      personId: string
      total: number
      candidates: {
        id: string
        name: string
        sources: string[]
        score: number
        evidence: string[]
        combined: string
      }[]
    }>(`/api/people/${encodeURIComponent(id)}/links`, signal),

  /** Points the server at a different workspace, validated before it is adopted. */
  switchWorkspace: async (path: string): Promise<{ root: string; data: string }> => {
    const response = await fetch('/api/workspace', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ path }),
    })

    if (!response.ok) {
      throw await toError(response)
    }

    return (await response.json()) as { root: string; data: string }
  },

  /** Lists a directory's children, so the workspace picker can be navigated. */
  browse: (path?: string, signal?: AbortSignal) =>
    request<{
      path: string
      parent: string | null
      isWorkspace: boolean
      directories: { name: string; path: string; isWorkspace: boolean }[]
    }>(`/api/workspace/browse${path ? `?path=${encodeURIComponent(path)}` : ''}`, signal),

  /** Records a person's gender, preserved exactly as typed. */
  setGender: async (id: string, gender: string | null): Promise<void> => {
    await patch(`/api/people/${encodeURIComponent(id)}/gender`, { gender })
  },

  /** Guest lists: named sets of people, with counts. */
  guestLists: (signal?: AbortSignal) =>
    request<{
      total: number
      lists: { slug: string; name: string; count: number; missing: number }[]
    }>('/api/guest-lists', signal),

  /** One guest list, grouped by recorded gender. */
  guestList: (slug: string, signal?: AbortSignal) =>
    request<{
      slug: string
      name: string
      notes: string | null
      count: number
      missing: string[]
      groups: {
        label: string
        unrecorded: boolean
        count: number
        people: { id: string; name: string }[]
      }[]
    }>(`/api/guest-lists/${encodeURIComponent(slug)}`, signal),

  /** Creates or replaces a guest list. */
  saveGuestList: async (payload: {
    slug: string
    name: string
    notes?: string
    members: string[]
  }): Promise<void> => {
    await patch(`/api/guest-lists/${encodeURIComponent(payload.slug)}`, {
      name: payload.name,
      notes: payload.notes,
      members: payload.members,
    })
  },

  /** Places recorded on any person, most-used first. Derived, never stored. */
  places: (signal?: AbortSignal) =>
    request<{ total: number; places: { value: string; count: number }[] }>('/api/places', signal),

  /** How-you-know-them values in use, derived from the people who have one. */
  connections: (signal?: AbortSignal) =>
    request<ChannelsResponse>('/api/connections', signal),

  /** Adds an account for a platform with no export, e.g. a Signal number. */
  addAccount: async (
    id: string,
    platform: string,
    handle: string,
    profileUrl?: string,
  ): Promise<AccountSummary[]> => {
    const result = await post<{ accounts: AccountSummary[] }>(
      `/api/people/${encodeURIComponent(id)}/accounts`,
      { platform, handle, profileUrl },
    )
    return result.accounts
  },

  /** Removes an account by platform and handle. */
  removeAccount: async (id: string, platform: string, handle: string): Promise<AccountSummary[]> => {
    const qs = `platform=${encodeURIComponent(platform)}&handle=${encodeURIComponent(handle)}`
    const result = await del2<{ accounts: AccountSummary[] }>(
      `/api/people/${encodeURIComponent(id)}/accounts?${qs}`,
    )
    return result.accounts
  },

  /** Renames a person. The id is unchanged, so notes and merges stay attached. */
  rename: async (id: string, name: string): Promise<void> => {
    await patch(`/api/people/${encodeURIComponent(id)}/name`, { name })
  },

  /** Records how you know someone; the server returns a didYouMean hint on a near-match. */
  setHowWeMet: async (
    id: string,
    howWeMet: string | null,
  ): Promise<{ didYouMean: string | null }> =>
    patch(`/api/people/${encodeURIComponent(id)}/met`, { howWeMet }),

  /** Sets the preferred channel; the server returns a didYouMean hint on a near-match. */
  setPreferred: async (
    id: string,
    channel: string | null,
  ): Promise<{ didYouMean: string | null }> =>
    patch(`/api/people/${encodeURIComponent(id)}/preferred`, { channel }),

  /** Merges `absorbed` into `surviving`; the survivor keeps its id. */
  merge: async (surviving: string, absorbed: string): Promise<string> => {
    const result = await post<{ mergeId: string }>('/api/merges', { surviving, absorbed })
    return result.mergeId
  },

  unmerge: async (mergeId: string): Promise<void> => {
    await del(`/api/merges/${encodeURIComponent(mergeId)}`)
  },

  merges: (signal?: AbortSignal) =>
    request<{
      merges: {
        id: string
        at: string
        survivorId: string
        absorbedId: string
        reason: string | null
        undone: boolean
      }[]
    }>('/api/merges', signal),

  /** Appends a timestamped entry without replacing anything already written. */
  appendNote: async (id: string, body: string): Promise<void> => {
    const response = await fetch(`/api/people/${encodeURIComponent(id)}/note/log`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ body }),
    })

    if (!response.ok) {
      throw new ApiError(response.statusText || 'Could not add the entry', 'Unknown', null, response.status)
    }
  },
}
