<script lang="ts">
  import { api, ApiError, type Channel, type LocationSummary } from './api'
  import { TIERS } from './format'

  let {
    personId,
    tier,
    tags,
    locations,
    preferred,
    howWeMet,
    onchange,
  }: {
    personId: string
    tier: string
    tags: string[]
    locations: LocationSummary[]
    preferred: string | null
    howWeMet: string | null
    onchange: () => void
  } = $props()

  // Channels come from the server, derived from what people actually use. Nothing is cached
  // here beyond the current render, so a channel disappears as soon as its last user changes.
  // Stored as { value, label } so the select can show "WhatsApp" while writing "whatsapp".
  let channels = $state<{ value: string; label: string }[]>([])
  let suggested = $state(false)

  let tagInput = $state('')
  let placeInput = $state('')
  let kindInput = $state('other')
  let busy = $state(false)
  let error = $state<string | null>(null)

  $effect(() => {
    api
      .channels()
      .then((result) => {
        channels = result.inUse.map((c: Channel) => ({ value: c.value, label: c.label }))
        suggested = result.suggested
      })
      .catch(() => {
        channels = []
      })
  })

  // Draft mirrors the saved value; blur and Enter commit it. Unchanged text is not
  // written, so clicking through contacts does not churn the files.
  let metDraft = $state('')

  let lastPerson = $state<string | null>(null)
  $effect(() => {
    if (personId === lastPerson) {
      return
    }

    lastPerson = personId
    metDraft = howWeMet ?? ''
  })

  const saveMet = () =>
    run(async () => {
      const value = metDraft.trim()

      if (value === (howWeMet ?? '')) {
        return
      }

      await api.setHowWeMet(personId, value || null)
    })

  // The dropdown is the only way to set a channel now, so there is no free-text path and
  // no typo warning. Channels still come from the server, derived from what is in use.
  const setPreferred = (value: string) => run(async () => {
    await api.setPreferred(personId, value || null)
  })

  // Excludes "all", which is a filter rather than a relationship.
  const choices = TIERS.filter((t) => t.value !== 'all')

  function describe(e: unknown): string {
    if (e instanceof ApiError) return e.hint ? `${e.message} — ${e.hint}` : e.message
    return e instanceof Error ? e.message : String(e)
  }

  async function run(action: () => Promise<void>) {
    if (busy) return
    busy = true
    error = null
    try {
      await action()
      onchange()
    } catch (e) {
      error = describe(e)
    } finally {
      busy = false
    }
  }

  const setTier = (value: string) => run(async () => { await api.setTier(personId, value) })

  const addTag = () =>
    run(async () => {
      const value = tagInput.trim()
      if (!value) return
      await api.changeTags(personId, [value], [])
      tagInput = ''
    })

  const removeTag = (tag: string) => run(async () => { await api.changeTags(personId, [], [tag]) })

  const addLocation = () =>
    run(async () => {
      const place = placeInput.trim()
      if (!place) return
      await api.setLocation(personId, place, kindInput)
      placeInput = ''
      kindInput = 'other'
    })

  const removeLocation = (place: string) => run(async () => { await api.setLocation(personId, place, 'other', true) })
</script>

{#if error}
  <p class="note-error" role="alert">{error}</p>
{/if}

<section>
  <h3>How we met</h3>

  <!-- Free text, not a dropdown. This is a human description - "Maya's wedding, 2019",
       "the climbing gym on Bloor" - and a fixed vocabulary forced it into categories it
       does not fit. Tags already provide the grouping vocabulary for the graph; this field
       is for the sentence. -->
  <textarea
    class="met-input"
    bind:value={metDraft}
    rows="2"
    placeholder="e.g. Met at Maya's wedding in 2019; we worked together at Acme"
    aria-label="How you met"
    disabled={busy}
    onblur={saveMet}
    onkeydown={(e) => {
      // Enter saves, Shift+Enter adds a line: a description may span two lines but saving
      // should not require reaching for the mouse.
      if (e.key === 'Enter' && !e.shiftKey) {
        e.preventDefault()
        saveMet()
      }
    }}
  ></textarea>
</section>

<section>
  <h3>Preferred method of comm.</h3>

  <div class="inline-add">
    <select
      value={preferred ?? ''}
      aria-label="Preferred method of comm."
      onchange={(e) => setPreferred((e.target as HTMLSelectElement).value)}
      disabled={busy}
    >
      <option value="">Not set</option>
      {#each channels as channel (channel.value)}
        <option value={channel.value}>{channel.label}</option>
      {/each}

      <!-- Keep the current value selectable even if it is no longer in use elsewhere:
           otherwise the dropdown would silently show "Not set" for a person who has one. -->
      {#if preferred && !channels.some((c) => c.value === preferred)}
        <option value={preferred}>{preferred}</option>
      {/if}
    </select>
  </div>

  {#if suggested && channels.length > 0}
    <p class="muted hint-line">Suggested until someone sets one.</p>
  {/if}

</section>

<section>
  <h3>Relationship</h3>
  <div class="tiers tiers-inline" role="group" aria-label="Relationship">
    {#each choices as choice (choice.value)}
      <button
        class:active={tier === choice.value}
        disabled={busy}
        onclick={() => setTier(choice.value)}
      >
        {choice.label}
      </button>
    {/each}
  </div>
</section>

<section>
  <h3>Tags</h3>

  {#if tags.length > 0}
    <ul class="tag-list" aria-label="Tags">
      {#each tags as tag (tag)}
        <li>
          <span class="tag">{tag}</span>
          <button
            class="tag-remove"
            title="Remove {tag}"
            aria-label="Remove tag {tag}"
            disabled={busy}
            onclick={() => removeTag(tag)}
          >×</button>
        </li>
      {/each}
    </ul>
  {:else}
    <p class="muted">No tags yet.</p>
  {/if}

  <div class="inline-add">
    <input
      bind:value={tagInput}
      placeholder="Add a tag…"
      aria-label="Add a tag"
      onkeydown={(e) => { if (e.key === 'Enter') addTag() }}
    />
    <button disabled={busy || !tagInput.trim()} onclick={addTag}>Add</button>
  </div>
</section>

<section>
  <h3>Locations</h3>

  {#if locations.length > 0}
    <ul class="plain">
      {#each locations as location (location.place + location.kind)}
        <li class="location-row">
          <span>{location.place}</span>
          {#if location.kind !== 'other'}
            <span class="reason">({location.kind})</span>
          {/if}
          <button
            class="tag-remove"
            title="Remove {location.place}"
            aria-label="Remove location {location.place}"
            disabled={busy}
            onclick={() => removeLocation(location.place)}>×</button>
        </li>
      {/each}
    </ul>
  {:else}
    <p class="muted">No locations yet.</p>
  {/if}

  <div class="inline-add">
    <input
      bind:value={placeInput}
      placeholder="Add a place…"
      aria-label="Add a location"
      onkeydown={(e) => { if (e.key === 'Enter') addLocation() }}
    />
    <select bind:value={kindInput} aria-label="Location kind">
      <option value="other">other</option>
      <option value="home">home</option>
      <option value="work">work</option>
    </select>
    <button disabled={busy || !placeInput.trim()} onclick={addLocation}>Add</button>
  </div>
</section>
