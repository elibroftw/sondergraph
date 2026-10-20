<script lang="ts">
  import { api, ApiError, type LinkCandidate } from './api'

  let { onlinked }: { onlinked: () => void } = $props()

  let candidates = $state<LinkCandidate[]>([])
  let total = $state(0)
  let loading = $state(true)
  let busy = $state<string | null>(null)
  let error = $state<string | null>(null)

  async function load() {
    loading = true
    error = null

    try {
      const result = await api.links()
      candidates = result.candidates
      total = result.total
    } catch (e) {
      error = describe(e)
    } finally {
      loading = false
    }
  }

  function describe(e: unknown): string {
    if (e instanceof ApiError) return e.hint ? `${e.message} — ${e.hint}` : e.message
    return e instanceof Error ? e.message : String(e)
  }

  /**
   * Links a pair, keeping the left record.
   *
   * The survivor is the left one because that is the order the evidence is presented in;
   * the absorbed record can be restored with ungraft either way.
   */
  async function link(candidate: LinkCandidate) {
    const key = candidate.left.id + candidate.right.id
    busy = key
    error = null

    try {
      await api.merge(candidate.left.id, candidate.right.id)
      onlinked()
      await load()
    } catch (e) {
      error = describe(e)
    } finally {
      busy = null
    }
  }

  $effect(() => {
    load()
  })
</script>

<section class="links">
  <h3>
    Possible links
    {#if total > 0}<span class="state">{total}</span>{/if}
  </h3>

  {#if error}
    <p class="note-error" role="alert">{error}</p>
  {/if}

  {#if loading}
    <p class="muted">Looking for cross-platform pairs…</p>
  {:else if candidates.length === 0}
    <p class="muted">No cross-platform matches left.</p>
  {:else}
    <!-- Deliberately capped: this is a review queue, not a report. 64 pairs is a session of
         work, and showing them all at once invites rubber-stamping. -->
    <ul class="link-list">
      {#each candidates.slice(0, 12) as candidate (candidate.left.id + candidate.right.id)}
        <li>
          <div class="link-pair">
            <span class="link-name">{candidate.left.name}</span>
            <span class="link-sources">{candidate.left.sources.join(', ')}</span>
          </div>
          <div class="link-pair">
            <span class="link-name">{candidate.right.name}</span>
            <span class="link-sources">{candidate.right.sources.join(', ')}</span>
          </div>
          <p class="link-evidence">
            {candidate.evidence.map((e) => e.detail).join('; ')} · combines {candidate.combined}
          </p>
          <button
            disabled={busy !== null}
            onclick={() => link(candidate)}
          >
            {busy === candidate.left.id + candidate.right.id ? 'Linking…' : 'These are the same person'}
          </button>
        </li>
      {/each}
    </ul>

    {#if total > 12}
      <p class="muted hint-line">
        Showing 12 of {total}. Linking these will reveal the next batch.
      </p>
    {/if}
  {/if}
</section>
