<script lang="ts">
  import { api, ApiError } from './api'

  let { onclose }: { onclose: () => void } = $props()

  let lists = $state<{ slug: string; name: string; count: number; missing: number }[]>([])
  let active = $state<string | null>(null)
  let detail = $state<{
    slug: string
    name: string
    notes: string | null
    count: number
    missing: string[]
    groups: { label: string; unrecorded: boolean; count: number; people: { id: string; name: string }[] }[]
  } | null>(null)
  let loading = $state(true)
  let error = $state<string | null>(null)

  async function load() {
    loading = true
    error = null

    try {
      const result = await api.guestLists()
      lists = result.lists

      if (active === null && lists.length > 0) {
        await open(lists[0].slug)
      } else if (active !== null) {
        await open(active)
      }
    } catch (e) {
      error = describe(e)
    } finally {
      loading = false
    }
  }

  async function open(slug: string) {
    active = slug
    detail = await api.guestList(slug)
  }

  function describe(e: unknown): string {
    if (e instanceof ApiError) return e.hint ? `${e.message} — ${e.hint}` : e.message
    return e instanceof Error ? e.message : String(e)
  }

  $effect(() => {
    load()
  })
</script>

<div class="guest-panel">
  <header>
    <h2>Guest lists</h2>
    <button class="guest-close" aria-label="Close guest lists" onclick={onclose}>×</button>
  </header>

  {#if error}
    <p class="note-error" role="alert">{error}</p>
  {/if}

  <div class="guest-body">
    <ul class="guest-lists">
      {#each lists as list (list.slug)}
        <li>
          <button class:active={list.slug === active} onclick={() => open(list.slug)}>
            <span class="guest-name">{list.name}</span>
            <span class="guest-count">{list.count}</span>
            {#if list.missing > 0}
              <!-- A list that silently shrinks is worse than one that says so. -->
              <span class="guest-missing" title="{list.missing} entries no longer resolve">
                {list.missing} missing
              </span>
            {/if}
          </button>
        </li>
      {/each}

      {#if !loading && lists.length === 0}
        <li class="muted guest-empty">
          No guest lists yet. Create one with<br />
          <code>sg guest add "June 2027" &lt;person&gt;…</code>
        </li>
      {/if}
    </ul>

    <div class="guest-detail">
      {#if loading}
        <p class="muted">Loading…</p>
      {:else if detail}
        <h3>{detail.name} <span class="guest-total">{detail.count} guests</span></h3>

        {#if detail.notes}
          <p class="muted">{detail.notes}</p>
        {/if}

        {#each detail.groups as group (group.label)}
          <section class="guest-group">
            <h4>
              {group.label}
              <span class="guest-total">{group.count}</span>
            </h4>
            <ul>
              {#each group.people as person (person.id)}
                <li>{person.name}</li>
              {/each}
            </ul>
          </section>
        {/each}

        {#if detail.missing.length > 0}
          <p class="guest-missing-block" role="status">
            {detail.missing.length} entr(y/ies) no longer resolve: {detail.missing.join(', ')}
          </p>
        {/if}
      {:else}
        <p class="muted">Select a list.</p>
      {/if}
    </div>
  </div>
</div>
