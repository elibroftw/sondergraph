<script lang="ts">
  import { api, ApiError, type LocationSummary } from './api'
  import { TIERS } from './format'

  let {
    personId,
    tier,
    tags,
    locations,
    onchange,
  }: {
    personId: string
    tier: string
    tags: string[]
    locations: LocationSummary[]
    onchange: () => void
  } = $props()

  let tagInput = $state('')
  let placeInput = $state('')
  let kindInput = $state('other')
  let busy = $state(false)
  let error = $state<string | null>(null)

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
