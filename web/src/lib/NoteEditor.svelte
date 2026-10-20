<script lang="ts">
  import { api, ApiError } from './api'
  import { renderMarkdown } from './markdown'

  let { personId, personName }: { personId: string; personName: string } = $props()

  let body = $state('')
  let saved = $state('')
  let loading = $state(true)
  let saving = $state(false)
  let error = $state<string | null>(null)
  let editing = $state(false)
  let entry = $state('')

  // Load whenever the selected person changes. Cancel the in-flight request when the
  // selection moves again, so a slow response for person A cannot overwrite person B's note.
  $effect(() => {
    const id = personId
    const controller = new AbortController()

    loading = true
    error = null
    editing = false

    api
      .getNote(id, controller.signal)
      .then((result) => {
        body = result.body
        saved = result.body
        loading = false
      })
      .catch((e) => {
        if (controller.signal.aborted) return
        error = describe(e)
        loading = false
      })

    return () => controller.abort()
  })

  const dirty = $derived(body !== saved)

  function describe(e: unknown): string {
    if (e instanceof ApiError) return e.hint ? `${e.message} — ${e.hint}` : e.message
    return e instanceof Error ? e.message : String(e)
  }

  async function save() {
    if (!dirty || saving) return

    saving = true
    error = null

    try {
      await api.saveNote(personId, body)
      saved = body
      editing = false
    } catch (e) {
      // The edit is deliberately left in the textarea on failure: discarding what someone
      // just wrote because a request failed would be the worst possible response.
      error = describe(e)
    } finally {
      saving = false
    }
  }

  async function revert() {
    body = saved
    editing = false
    error = null
  }

  async function addEntry() {
    if (!entry.trim() || saving) return

    saving = true
    error = null

    try {
      await api.appendNote(personId, entry)
      const refreshed = await api.getNote(personId)
      body = refreshed.body
      saved = refreshed.body
      entry = ''
      editing = false
    } catch (e) {
      error = describe(e)
    } finally {
      saving = false
    }
  }

  function onKeydown(event: KeyboardEvent) {
    // Ctrl/Cmd+Enter saves, matching every other writing surface. Escape reverts.
    if (event.key === 'Enter' && (event.metaKey || event.ctrlKey)) {
      event.preventDefault()
      save()
    } else if (event.key === 'Escape') {
      if (dirty) revert()
      else editing = false
    }
  }
</script>

<section class="notes">
  <h3>
    Notes
    {#if saving}<span class="state">saving…</span>{/if}
    {#if dirty && !saving}<span class="state unsaved">unsaved</span>{/if}
  </h3>

  {#if error}
    <p class="note-error" role="alert">{error}</p>
  {/if}

  {#if loading}
    <p class="muted">Loading…</p>
  {:else if editing}
    <textarea
      bind:value={body}
      onkeydown={onKeydown}
      placeholder="What matters about {personName}? Markdown works."
      aria-label="Note about {personName}"
      spellcheck="true"
      rows="8"></textarea>

    <div class="actions">
      <button class="primary" onclick={save} disabled={saving || !dirty}>
        {saving ? 'Saving…' : 'Save'}
      </button>
      <button onclick={revert} disabled={saving}>Revert</button>
      <span class="hint">Ctrl+Enter to save · Esc to cancel</span>
    </div>
  {:else}
    <!-- No empty-state text here: an "Add note" button already says the note does not exist,
         and saying it twice in the same few pixels is noise. -->
    {#if saved.trim()}
      <div class="rendered">{@html renderMarkdown(saved)}</div>
    {/if}

    <div class="actions">
      <button onclick={() => (editing = true)}>{saved.trim() ? 'Edit note' : 'Add note'}</button>
    </div>
  {/if}

  <!-- A dated log entry is a separate action from editing the note, because the two are
       different habits: rewriting a document, versus recording something that happened. -->
  <div class="entry">
    <input
      bind:value={entry}
      placeholder="Add a dated entry…"
      aria-label="New entry about {personName}"
      onkeydown={(e) => { if (e.key === 'Enter') addEntry() }}
    />
    <button onclick={addEntry} disabled={saving || !entry.trim()}>Add</button>
  </div>
</section>
