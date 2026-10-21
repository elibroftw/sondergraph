<script lang="ts">
  import { api, ApiError } from './api'

  let {
    current,
    onswitched,
  }: { current: string | null; onswitched: () => void } = $props()

  let browsing = $state(false)
  let path = $state(current ?? '')
  let parent = $state<string | null>(null)
  let isWorkspace = $state(false)
  let directories = $state<{ name: string; path: string; isWorkspace: boolean }[]>([])
  let error = $state<string | null>(null)
  let busy = $state(false)

  async function open(start?: string) {
    error = null

    try {
      const result = await api.browse(start)
      path = result.path
      parent = result.parent
      isWorkspace = result.isWorkspace
      directories = result.directories
      browsing = true
    } catch (e) {
      error = describe(e)
    }
  }

  async function choose(target: string) {
    busy = true
    error = null

    try {
      await api.switchWorkspace(target)
      browsing = false
      onswitched()
    } catch (e) {
      // The server keeps its previous workspace on a refusal, so this is recoverable.
      error = describe(e)
    } finally {
      busy = false
    }
  }

  function describe(e: unknown): string {
    if (e instanceof ApiError) return e.hint ? `${e.message} — ${e.hint}` : e.message
    return e instanceof Error ? e.message : String(e)
  }

  function shorten(value: string): string {
    // Show the last two segments: a full path does not fit in a header.
    const parts = value.split('/').filter(Boolean)
    return parts.length > 2 ? '…/' + parts.slice(-2).join('/') : value
  }
</script>

<button class="workspace-btn" onclick={() => open()} title={current ?? 'No workspace'}>
  <span class="workspace-label">data</span>
  <span class="workspace-path">{current ? shorten(current) : 'not set'}</span>
</button>

{#if browsing}
  <div class="picker-backdrop" role="presentation" onclick={() => (browsing = false)}></div>

  <div class="picker" role="dialog" aria-label="Choose data directory" aria-modal="true">
    <header>
      <h3>Data directory</h3>
      <button class="picker-close" aria-label="Close" onclick={() => (browsing = false)}>×</button>
    </header>

    <p class="picker-path" title={path}>{path}</p>

    <div class="picker-actions">
      {#if parent}
        <button disabled={busy} onclick={() => open(parent)}>↑ Up</button>
      {/if}
      {#if isWorkspace}
        <button class="primary" disabled={busy} onclick={() => choose(path)}>
          {busy ? 'Switching…' : 'Use this directory'}
        </button>
      {:else}
        <span class="muted picker-hint">Not a workspace yet. Open it and run sg init.</span>
      {/if}
    </div>

    {#if error}
      <p class="note-error" role="alert">{error}</p>
    {/if}

    <ul class="picker-list">
      {#each directories as directory (directory.path)}
        <li>
          <button disabled={busy} onclick={() => open(directory.path)}>
            <span class="picker-name">{directory.name}</span>
            {#if directory.isWorkspace}<span class="picker-badge">workspace</span>{/if}
          </button>
        </li>
      {/each}
      {#if directories.length === 0}
        <li class="muted picker-empty">No subdirectories.</li>
      {/if}
    </ul>
  </div>
{/if}
