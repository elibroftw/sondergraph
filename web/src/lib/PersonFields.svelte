<script lang="ts">
  import { api, ApiError, type Channel, type LocationSummary } from './api'
  import { readableName, TIERS } from './format'

  let {
    personId,
    tier,
    tags,
    locations,
    preferred,
    howWeMet,
    gender,
    friendsWith,
    onchange,
  }: {
    personId: string
    tier: string
    tags: string[]
    locations: LocationSummary[]
    preferred: string | null
    howWeMet: string | null
    gender: string | null
    friendsWith: string[]
    onchange: () => void
  } = $props()

  // Friend ids resolved to names, for display. Loaded lazily because the list is only
  // meaningful once someone has recorded a friendship.
  let friendNames = $state<Record<string, string>>({})
  let friendQuery = $state('')
  let friendMatches = $state<{ id: string; name: string }[]>([])
  let friendBusy = $state(false)
  let friendError = $state<string | null>(null)

  // Defensive against a response that omits the field: a missing array must render as an
  // empty list, not throw inside the panel and leave it stuck loading.
  const friends = $derived(friendsWith ?? [])

  $effect(() => {
    if (friends.length === 0) {
      friendNames = {}
      return
    }

    api
      .people(friendsWith.join(' '), 'all')
      .then((page) => {
        const map: Record<string, string> = {}
        for (const person of page.people) map[person.id] = person.name
        friendNames = map
      })
      .catch(() => {
        friendNames = {}
      })
  })

  async function searchFriends() {
    const q = friendQuery.trim()

    if (q.length < 2) {
      friendMatches = []
      return
    }

    try {
      const page = await api.people(q, 'all')
      // Exclude the person themselves and anyone already listed.
      friendMatches = page.people
        .filter((p) => p.id !== personId && !friends.includes(p.id))
        .slice(0, 6)
        .map((p) => ({ id: p.id, name: p.name }))
    } catch {
      friendMatches = []
    }
  }

  async function linkFriend(id: string) {
    friendBusy = true
    friendError = null

    try {
      await api.addFriend(personId, id)
      friendQuery = ''
      friendMatches = []
      onchange()
    } catch (e) {
      friendError = e instanceof ApiError ? e.message : String(e)
    } finally {
      friendBusy = false
    }
  }

  async function unlinkFriend(id: string) {
    friendBusy = true
    friendError = null

    try {
      await api.removeFriend(personId, id)
      onchange()
    } catch (e) {
      friendError = e instanceof ApiError ? e.message : String(e)
    } finally {
      friendBusy = false
    }
  }

  // Channels come from the server, derived from what people actually use. Nothing is cached
  // here beyond the current render, so a channel disappears as soon as its last user changes.
  // Stored as { value, label } so the select can show "WhatsApp" while writing "whatsapp".
  let channels = $state<{ value: string; label: string }[]>([])

  let tagInput = $state('')
  let placeInput = $state('')
  let kindInput = $state('other')
  let busy = $state(false)
  let error = $state<string | null>(null)

  $effect(() => {
    api
      .channels()
      .then((result) => {
        // The server returns every selectable channel. Showing only in-use ones left a
        // fresh workspace with a near-empty dropdown and no way to choose anything that had
        // not already been picked - which, with the create-new flow gone, meant some
        // channels were simply unreachable.
        channels = result.channels.map((c) => ({ value: c.value, label: c.label }))
      })
      .catch(() => {
        channels = []
      })
  })

  // Draft mirrors the saved value; blur and Enter commit it. Unchanged text is not
  // written, so clicking through contacts does not churn the files.
  let metDraft = $state('')
  let genderDraft = $state('')

  let lastPerson = $state<string | null>(null)
  $effect(() => {
    if (personId === lastPerson) {
      return
    }

    lastPerson = personId
    metDraft = howWeMet ?? ''
    genderDraft = gender ?? ''
  })

  const saveGender = () =>
    run(async () => {
      const value = genderDraft.trim()

      if (value === (gender ?? '')) {
        return
      }

      await api.setGender(personId, value || null)
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
  <h3>Notes</h3>

  <!-- The free-text note about this person: how you met, where they work, what to remember.
       It replaced a separate markdown note editor, which duplicated this field with more
       machinery. Existing note files were migrated into it, since their contents were the
       same kind of thing - "Met in 2021", "Elementary school". -->
  <textarea
    class="met-input"
    bind:value={metDraft}
    rows="2"
    placeholder="e.g. Met at Maya's wedding in 2019; we worked together at Acme"
    aria-label="Notes"
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
  <h3>Friends with</h3>

  <!-- Friendship is symmetric, so adding here also adds the other person. The list is what
       makes a network view possible: without edges the graph is only you and everyone. -->
  <ul class="friends-list">
    {#each friends as id (id)}
      <li>
        <span class="friends-name">{readableName(friendNames[id] ?? id)}</span>
        <button disabled={friendBusy} onclick={() => unlinkFriend(id)} aria-label="Unlink {id}">×</button>
      </li>
    {/each}
  </ul>

  {#if friends.length === 0}
    <p class="muted hint-line">No friends recorded.</p>
  {/if}

  <div class="inline-add">
    <input
      bind:value={friendQuery}
      placeholder="Link a contact…"
      aria-label="Search contacts to link as friends"
      oninput={searchFriends}
    />
  </div>

  {#if friendMatches.length > 0}
    <ul class="friends-matches">
      {#each friendMatches as match (match.id)}
        <li>
          <button disabled={friendBusy} onclick={() => linkFriend(match.id)}>{match.name}</button>
        </li>
      {/each}
    </ul>
  {/if}

  {#if friendError}
    <p class="note-error" role="alert">{friendError}</p>
  {/if}
</section>

<section>
  <h3>Gender</h3>

  <!-- A two-option dropdown, as requested. "Not set" is a real choice rather than a gap:
       most contacts have no gender recorded, and clearing one has to be possible.
       A value already stored that is neither M nor F is kept and shown rather than
       silently dropped - the inferred values in this workspace are all M or F, but a
       record written by an older version or by hand should not be destroyed by opening
       the panel. -->
  <select
    class="gender-input"
    value={genderDraft}
    aria-label="Gender"
    disabled={busy}
    onchange={(e) => {
      genderDraft = (e.target as HTMLSelectElement).value
      saveGender()
    }}
  >
    <option value="">Not set</option>
    <option value="M">M</option>
    <option value="F">F</option>
    {#if genderDraft && genderDraft !== 'M' && genderDraft !== 'F'}
      <option value={genderDraft}>{genderDraft}</option>
    {/if}
  </select>
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
