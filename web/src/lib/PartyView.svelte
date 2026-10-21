<script lang="ts">
  import { api, ApiError } from './api'

  let { onclose }: { onclose: () => void } = $props()

  type Summary = { slug: string; name: string; count: number; missing: number }
  type Detail = {
    slug: string
    name: string
    notes: string | null
    date: string | null
    venue: string | null
    count: number
    missing: string[]
    groups: { label: string; unrecorded: boolean; count: number; people: { id: string; name: string }[] }[]
  }
  type Suggestion = {
    id: string
    name: string
    tierLabel: string
    gender: string | null
    distanceKm: number | null
    weight: number
  }

  let parties = $state<Summary[]>([])
  let active = $state<string | null>(null)
  let detail = $state<Detail | null>(null)
  let suggestions = $state<Suggestion[]>([])
  let notes = $state<string[]>([])
  let members = $state<string[]>([])

  // Editable party fields.
  let name = $state('')
  let date = $state('')
  let venue = $state('')

  let loading = $state(true)
  let busy = $state(false)
  let error = $state<string | null>(null)
  let creating = $state(false)

  function describe(e: unknown): string {
    if (e instanceof ApiError) return e.hint ? `${e.message} — ${e.hint}` : e.message
    return e instanceof Error ? e.message : String(e)
  }

  async function loadParties() {
    loading = true
    error = null

    try {
      const result = await api.guestLists()
      parties = result.lists
    } catch (e) {
      error = describe(e)
    } finally {
      loading = false
    }
  }

  async function open(slug: string) {
    active = slug
    error = null

    try {
      const d = await api.guestList(slug)
      detail = d as Detail
      members = d.groups.flatMap((g) => g.people.map((p) => p.id))
      name = d.name
      date = d.date ?? ''
      venue = d.venue ?? ''
      await loadSuggestions()
    } catch (e) {
      error = describe(e)
    }
  }

  async function loadSuggestions() {
    if (!active) return

    try {
      const result = await api.partySuggestions(active, Math.max(members.length, 20))
      suggestions = result.suggestions
      notes = result.notes
    } catch (e) {
      error = describe(e)
    }
  }

  async function save() {
    if (!active) return

    busy = true
    error = null

    try {
      await api.saveGuestList({
        slug: active,
        name: name.trim() || active,
        date: date || null,
        venue: venue || null,
        members,
      })

      await loadParties()
      await open(active)
    } catch (e) {
      error = describe(e)
    } finally {
      busy = false
    }
  }

  function accept(person: { id: string }) {
    if (!members.includes(person.id)) {
      members = [...members, person.id]
      suggestions = suggestions.filter((s) => s.id !== person.id)
    }
  }

  function decline(person: { id: string }) {
    suggestions = suggestions.filter((s) => s.id !== person.id)
  }

  function remove(id: string) {
    members = members.filter((m) => m !== id)
  }

  async function create() {
    const slug = name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '')

    if (!slug) {
      error = 'Give the party a name first.'
      return
    }

    busy = true
    error = null

    try {
      await api.saveGuestList({ slug, name: name.trim(), date: date || null, venue: venue || null, members: [] })
      creating = false
      name = ''
      date = ''
      venue = ''
      await loadParties()
      await open(slug)
    } catch (e) {
      error = describe(e)
    } finally {
      busy = false
    }
  }

  /** Everyone currently invited, with names resolved from the detail groups. */
  const invited = $derived(
    detail
      ? detail.groups.flatMap((g) => g.people.map((p) => ({ ...p, label: g.label })))
      : [],
  )

  const ratio = $derived.by(() => {
    const f = invited.filter((p) => p.label === 'F').length
    const m = invited.filter((p) => p.label === 'M').length
    return { f, m, other: invited.length - f - m }
  })

  $effect(() => {
    loadParties()
  })
</script>

<div class="party-view">
  <header class="party-head">
    <h1>Party planner</h1>
    <div class="party-head-actions">
      <button onclick={() => (creating = true)}>New party</button>
      <button class="party-close" onclick={onclose}>Back to contacts</button>
    </div>
  </header>

  {#if error}
    <p class="note-error" role="alert">{error}</p>
  {/if}

  <div class="party-body">
    <aside class="party-list">
      {#if loading}
        <p class="muted">Loading…</p>
      {:else if parties.length === 0 && !creating}
        <p class="muted">No parties yet.</p>
      {/if}

      <ul>
        {#each parties as party (party.slug)}
          <li>
            <button class:active={party.slug === active} onclick={() => open(party.slug)}>
              <span class="party-name">{party.name}</span>
              <span class="party-count">{party.count}</span>
            </button>
          </li>
        {/each}
      </ul>
    </aside>

    <main class="party-detail">
      {#if creating}
        <section class="party-card">
          <h2>New party</h2>
          <div class="party-fields">
            <label>Name<input bind:value={name} placeholder="Summer 2027" /></label>
            <label>Date<input type="date" bind:value={date} /></label>
            <label>Venue<input bind:value={venue} placeholder="Toronto" /></label>
          </div>
          <div class="party-actions">
            <button class="primary" disabled={busy} onclick={create}>Create</button>
            <button disabled={busy} onclick={() => (creating = false)}>Cancel</button>
          </div>
        </section>
      {:else if detail}
        <section class="party-card">
          <div class="party-fields">
            <label>Name<input bind:value={name} /></label>
            <label>Date<input type="date" bind:value={date} /></label>
            <label>Venue<input bind:value={venue} placeholder="Toronto" /></label>
          </div>

          <p class="party-tally">
            {invited.length} invited
            · {ratio.f} F / {ratio.m} M{ratio.other > 0 ? ` / ${ratio.other} unrecorded` : ''}
          </p>

          <div class="party-actions">
            <button class="primary" disabled={busy} onclick={save}>
              {busy ? 'Saving…' : 'Save party'}
            </button>
            <button disabled={busy} onclick={loadSuggestions}>Refresh suggestions</button>
          </div>
        </section>

        <div class="party-columns">
          <section class="party-card">
            <h2>Guest list <span class="party-count">{invited.length}</span></h2>

            {#if invited.length === 0}
              <p class="muted">Nobody invited yet. Accept suggestions, or add from contacts.</p>
            {/if}

            <ul class="party-guests">
              {#each invited as guest (guest.id)}
                <li>
                  <span class="party-guest-name">{guest.name}</span>
                  <span class="party-guest-label">{guest.label}</span>
                  <button onclick={() => remove(guest.id)} title="Remove" aria-label="Remove {guest.name}">x</button>
                </li>
              {/each}
            </ul>

            {#if detail.missing.length > 0}
              <p class="party-missing">{detail.missing.length} entr(y/ies) no longer resolve</p>
            {/if}
          </section>

          <section class="party-card">
            <h2>Suggestions <span class="party-count">{suggestions.length}</span></h2>

            {#if suggestions.length === 0}
              <p class="muted">No suggestions. Refresh, or add people by hand.</p>
            {/if}

            <ul class="party-suggestions">
              {#each suggestions as person (person.id)}
                <li>
                  <span class="party-guest-name">{person.name}</span>
                  <span class="party-guest-label">{person.tierLabel}</span>
                  {#if person.gender}<span class="party-guest-label">{person.gender}</span>{/if}
                  {#if person.distanceKm !== null}
                    <span class="party-guest-label">{person.distanceKm} km</span>
                  {/if}
                  <button class="accept" onclick={() => accept(person)} title="Add">+</button>
                  <button onclick={() => decline(person)} title="Not this one">×</button>
                </li>
              {/each}
            </ul>

            {#each notes as note (note)}
              <p class="party-note">{note}</p>
            {/each}
          </section>
        </div>
      {:else}
        <p class="muted">Select a party, or create one.</p>
      {/if}
    </main>
  </div>
</div>
