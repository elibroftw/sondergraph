<script lang="ts">
  import { api, ApiError, type PersonSummary } from './api'

  let {
    personId,
    personName,
    sources,
    onmerged,
  }: {
    personId: string
    personName: string
    sources: string[]
    onmerged: () => void
  } = $props()

  let open = $state(false)
  let query = $state('')
  let results = $state<PersonSummary[]>([])
  let target = $state<PersonSummary | null>(null)
  let busy = $state(false)
  let error = $state<string | null>(null)
  let searchTimer: ReturnType<typeof setTimeout> | undefined

  function describe(e: unknown): string {
    if (e instanceof ApiError) return e.hint ? `${e.message} — ${e.hint}` : e.message
    return e instanceof Error ? e.message : String(e)
  }

  async function search(value: string) {
    if (value.trim().length < 2) {
      results = []
      return
    }

    try {
      const page = await api.people(value, 'all')
      // Exclude the person being viewed: merging into yourself is meaningless.
      results = page.people.filter((p) => p.id !== personId).slice(0, 8)
    } catch (e) {
      error = describe(e)
    }
  }

  function onInput() {
    clearTimeout(searchTimer)
    searchTimer = setTimeout(() => search(query), 200)
  }

  async function merge() {
    if (!target || busy) return

    busy = true
    error = null

    try {
      await api.merge(personId, target.id)
      open = false
      query = ''
      results = []
      target = null
      onmerged()
    } catch (e) {
      error = describe(e)
    } finally {
      busy = false
    }
  }

  /** Accounts on this person that the candidate does not already have. */
  function newPlatforms(candidate: PersonSummary): string[] {
    const mine = new Set(sources)
    return candidate.sources.filter((s) => !mine.has(s))
  }
</script>

<section class="merge">
  <h3>Merge</h3>

  {#if error}
    <p class="note-error" role="alert">{error}</p>
  {/if}

  {#if !open}
    <p class="muted">
      Combine this contact with another record of the same person — for example an Instagram
      handle and a phone contact.
    </p>
    <button onclick={() => (open = true)}>Merge with another contact…</button>
  {:else}
    <p class="muted">
      Search for the other record of <strong>{personName}</strong>. This contact keeps its id;
      the other one is absorbed and can be restored later.
    </p>

    <div class="inline-add">
      <input
        bind:value={query}
        oninput={onInput}
        placeholder="Search name, handle or phone…"
        aria-label="Search for a contact to merge with"
      />
    </div>

    {#if results.length > 0}
      <ul class="merge-results" aria-label="Merge candidates">
        {#each results as candidate (candidate.id)}
          <li>
            <button
              class:chosen={target?.id === candidate.id}
              onclick={() => (target = candidate)}
            >
              <span class="merge-name">{candidate.name}</span>
              <span class="merge-meta">
                {candidate.sources.join(', ')}
                {#if candidate.phones.length > 0}· {candidate.phones.length} phone(s){/if}
              </span>
            </button>
          </li>
        {/each}
      </ul>
    {:else if query.trim().length >= 2}
      <p class="muted">No matches.</p>
    {/if}

    {#if target}
      <div class="merge-confirm">
        <p>
          Absorb <strong>{target.name}</strong> into <strong>{personName}</strong>?
        </p>

        {#if newPlatforms(target).length > 0}
          <p class="muted">Adds: {newPlatforms(target).join(', ')}</p>
        {/if}

        <div class="actions">
          <button class="primary" onclick={merge} disabled={busy}>
            {busy ? 'Merging…' : 'Merge'}
          </button>
          <button onclick={() => (target = null)} disabled={busy}>Cancel</button>
        </div>
      </div>
    {/if}

    <div class="actions">
      <button onclick={() => { open = false; results = []; target = null; query = '' }}>
        Close
      </button>
    </div>
  {/if}
</section>
