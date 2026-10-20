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

  people: (query: string, tier: string, signal?: AbortSignal) => {
    const params = new URLSearchParams()

    if (query.trim()) {
      params.set('q', query.trim())
    }

    if (tier && tier !== 'all') {
      params.set('tier', tier)
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
