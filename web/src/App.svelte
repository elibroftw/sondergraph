<script lang="ts">
  import { api, ApiError, type Health, type PersonDetail, type PersonSummary, type Stats } from './lib/api'
  import { initials, people as peopleLabel, platformHue, platformLabel, resolvePlatform, SOURCES, TIERS } from './lib/format'
  import PersonPanel from './lib/PersonPanel.svelte'
  import LinkReview from './lib/LinkReview.svelte'
  import WorkspacePicker from './lib/WorkspacePicker.svelte'
  import PartyView from './lib/PartyView.svelte'

  // ---- state -------------------------------------------------------------------------

  let health = $state<Health | null>(null)
  let stats = $state<Stats | null>(null)
  let contacts = $state<PersonSummary[]>([])
  let total = $state(0)

  let query = $state('')
  let tier = $state('all')
  let platform = $state('all')
  let place = $state('all')
  let places = $state<{ value: string; count: number }[]>([])
  let selectedId = $state<string | null>(null)
  let selected = $state<PersonDetail | null>(null)

  let showLinks = $state(false)
  let showGuests = $state(false)
  let loading = $state(true)
  let detailLoading = $state(false)
  let error = $state<string | null>(null)

  // Server-side filtering is debounced: every keystroke would otherwise be a request, and
  // the search is over the whole graph rather than the current page.
  let searchTimer: ReturnType<typeof setTimeout> | undefined

  // ---- data loading ------------------------------------------------------------------

  async function loadShell() {
    try {
      const [healthResult, statsResult] = await Promise.all([api.health(), api.stats()])
      health = healthResult
      stats = statsResult
    } catch (e) {
      error = describe(e)
    }
  }

  async function loadContacts() {
    loading = true
    error = null

    try {
      const page = await api.people(query, tier, platform, place)
      contacts = page.people
      total = page.total
    } catch (e) {
      error = describe(e)
      contacts = []
      total = 0
    } finally {
      loading = false
    }
  }

  async function selectPerson(id: string) {
    selectedId = id
    detailLoading = true

    try {
      selected = await api.person(id)
    } catch (e) {
      error = describe(e)
      selected = null
    } finally {
      detailLoading = false
    }
  }

  /**
   * Re-fetches the open person and the list after an edit.
   *
   * The list matters as much as the panel: changing a tier or renaming affects the row that
   * is displayed, and a stale list makes the edit look like it did not save.
   */
  async function refreshAfterEdit() {
    if (!selectedId) return

    try {
      selected = await api.person(selectedId)
    } catch (e) {
      error = describe(e)
    }

    // Only reload the list when the current filter could be affected: a tier change while
    // filtered by tier can move someone out of view.
    loadContacts()
    loadShell()
  }

  function onSearchInput() {
    clearTimeout(searchTimer)
    searchTimer = setTimeout(loadContacts, 180)
  }

  function onPlatformChange(next: string) {
    platform = next
    loadContacts()
  }

  function onPlaceChange(next: string) {
    place = next
    loadContacts()
  }

  // Places come from the server, derived from the people who have one, so the list cannot
  // accumulate entries nobody uses.
  $effect(() => {
    api
      .places()
      .then((result) => {
        places = result.places
      })
      .catch(() => {
        places = []
      })
  })

  // A smart query can contain its own filters, so the dropdown reflects what the query says.
  // Showing "All sources" while the query says platform:ig would be actively misleading.
  const queryPlatform = $derived(
    (() => {
      const raw = /\b(?:platform|source|src|sm):("?)([a-z]+)\1/i.exec(query)?.[2]
      return raw ? resolvePlatform(raw) : undefined
    })(),
  )
  const queryTier = $derived(/\b(?:tier|relationship|rel):("?)([a-z-]+)\1/i.exec(query)?.[2]?.toLowerCase())

  const activeFilters = $derived(
    [platform !== 'all', place !== 'all', tier !== 'all',
     queryPlatform !== undefined, queryTier !== undefined]
      .filter(Boolean).length,
  )

  function onTierChange(next: string) {
    tier = next
    loadContacts()
  }

  function clearFilters() {
    query = ''
    platform = 'all'
    place = 'all'
    tier = 'all'
    loadContacts()
  }

  function describe(e: unknown): string {
    if (e instanceof ApiError) {
      return e.hint ? `${e.message} — ${e.hint}` : e.message
    }

    return e instanceof Error ? e.message : String(e)
  }

  // Re-read everything after the data source changes, or the list would keep showing the
  // previous workspace until the next navigation.
  function reloadAll() {
    selectedId = null
    detailLoading = false
    loadShell()
    loadContacts()
  }

  $effect(() => {
    loadShell()
    loadContacts()
  })
</script>

{#if showGuests}
  <PartyView onclose={() => (showGuests = false)} />
{:else}
<div class="app">
  <header>
    <div class="brand">
      <h1>sondergraph</h1>
      <p class="tagline">everything, all at once</p>
    </div>

    <WorkspacePicker
      current={health?.workspace.ok ? health.workspace.root : null}
      onswitched={reloadAll}
    />

    {#if stats}
      <dl class="stats">
        <div><dt>people</dt><dd>{stats.people.toLocaleString()}</dd></div>
        <div><dt>reachable</dt><dd>{stats.withPhone.toLocaleString()}</dd></div>
        <div><dt>multi-source</dt><dd>{stats.multiSource.toLocaleString()}</dd></div>
      </dl>
    {/if}
  </header>

  {#if health && !health.workspace.ok}
    <!-- A missing workspace is the most likely first-run problem, so it gets a real
         explanation rather than an empty list. -->
    <div class="notice">
      <h2>No workspace found</h2>
      <p>{health.workspace.error}</p>
      {#if health.workspace.hint}<pre>{health.workspace.hint}</pre>{/if}
    </div>
  {:else}
    <div class="toolbar">
      <input
        class="search"
        type="search"
        placeholder="Search names, handles, phone numbers…"
        bind:value={query}
        oninput={onSearchInput}
        aria-label="Search contacts"
      />

      <div class="filters">
        <label class="source-filter">
          <span class="visually-hidden">Filter by source</span>
          <select
            value={SOURCES.some((o) => o.value === queryPlatform) ? queryPlatform : platform}
            onchange={(e) => onPlatformChange((e.target as HTMLSelectElement).value)}
            aria-label="Filter by source"
          >
            {#each SOURCES as option (option.value)}
              <option value={option.value}>{option.label}</option>
            {/each}
          </select>
        </label>

        <label class="source-filter">
          <span class="visually-hidden">Filter by location</span>
          <select
            value={place}
            onchange={(e) => onPlaceChange((e.target as HTMLSelectElement).value)}
            aria-label="Filter by location"
          >
            <option value="all">All locations</option>
            {#each places as option (option.value)}
              <option value={option.value}>{option.value}</option>
            {/each}
          </select>
        </label>

        <div class="tiers" role="tablist" aria-label="Filter by tier">
          {#each TIERS as option (option.value)}
            <button
              role="tab"
              aria-selected={tier === option.value || queryTier === option.value}
              class:active={tier === option.value || queryTier === option.value}
              onclick={() => onTierChange(option.value)}
            >
              {option.label}
            </button>
          {/each}
        </div>
      </div>

      <div class="toolbar-actions">
        <button class="link-toggle" onclick={() => (showLinks = !showLinks)}>
          {showLinks ? 'Hide possible links' : 'Review possible links…'}
        </button>

        <button class="link-toggle" onclick={() => (showGuests = true)}>Parties…</button>
      </div>

      {#if activeFilters > 0}
        <div class="query-hints" aria-live="polite">
          <span class="muted">{activeFilters} filter(s) active</span>
          <button onclick={clearFilters}>Clear</button>
        </div>
      {/if}
    </div>

    {#if error}
      <div class="error" role="alert">{error}</div>
    {/if}

    {#if showLinks}
      <LinkReview onlinked={refreshAfterEdit} />
    {/if}



    <div class="split">
      <section class="list" aria-label="Contacts">
        {#if loading}
          <p class="muted">Loading…</p>
        {:else if contacts.length === 0}
          <p class="muted">
            {query || tier !== 'all' || platform !== 'all' || place !== 'all'
              ? 'No one matches that filter.'
              : 'No contacts yet. Import an export with: sg ingest parse <path>'}
          </p>
        {:else}
          <ul>
            {#each contacts as person (person.id)}
              <li>
                <button
                  class:selected={person.id === selectedId}
                  onclick={() => selectPerson(person.id)}
                >
                  <span class="avatar" aria-hidden="true">{initials(person.name)}</span>

                  <span class="body">
                    <span class="name">{person.name}</span>

                    <span class="meta">
                      {#each person.accounts.slice(0, 3) as account (account.platform + account.handle)}
                        <span
                          class="chip"
                          style="--hue: {platformHue(account.platform)}"
                          title={account.platform}
                        >{platformLabel(account.platform)}</span>
                      {/each}

                      {#if person.phones.length > 0}
                        <span class="fact">☎ {person.phones.length}</span>
                      {/if}

                      {#if person.emails.length > 0}
                        <span class="fact">✉ {person.emails.length}</span>
                      {/if}

                      {#if person.tierLabel !== '-'}
                        <span class="tier">{person.tierLabel}</span>
                      {/if}
                    </span>
                  </span>
                </button>
              </li>
            {/each}
          </ul>

          {#if total > contacts.length}
            <p class="muted more">
              Showing {contacts.length.toLocaleString()} of {peopleLabel(total)}.
              Narrow the search to see more.
            </p>
          {/if}
        {/if}
      </section>

      <aside class="detail" aria-label="Contact detail">
        {#if detailLoading}
          <p class="muted">Loading…</p>
        {:else if selected}
          <PersonPanel person={selected} onchanged={refreshAfterEdit} />
        {:else}
          <p class="muted">Select a contact to see everything known about them.</p>
        {/if}
      </aside>
    </div>
  {/if}
</div>
{/if}