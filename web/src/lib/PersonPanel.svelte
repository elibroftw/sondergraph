<script lang="ts">
  import { api, ApiError, type PersonDetail } from './api'
  import { initials, platformHue, platformLabel } from './format'
  import PersonFields from './PersonFields.svelte'
  import MergeBox from './MergeBox.svelte'
  import SocialFrame from './SocialFrame.svelte'

  // Inline name editing. Instagram contacts arrive named after their handle, and there was
  // no way to give them a real first and last name without dropping to the CLI.
  let editingName = $state(false)
  let confirmingDelete = $state(false)
  let deleting = $state(false)
  let deleteError = $state<string | null>(null)
  let nameDraft = $state('')
  let savingName = $state(false)
  let nameError = $state<string | null>(null)

  /**
   * Deletes the contact and leaves the panel.
   *
   * The panel is emptied rather than refreshed: the person it was showing no longer exists, and
   * re-fetching would surface a 404 where a graceful exit belongs.
   */
  async function confirmDelete() {
    deleting = true
    deleteError = null

    try {
      await api.deletePerson(person.id)
      onchanged()
    } catch (e) {
      deleteError = e instanceof ApiError ? e.message : String(e)
    } finally {
      deleting = false
    }
  }

  function startEditingName() {
    nameDraft = person.name
    nameError = null
    editingName = true
  }

  async function saveName() {
    const value = nameDraft.trim()

    if (!value) {
      nameError = 'A name cannot be empty.'
      return
    }

    if (value === person.name) {
      editingName = false
      return
    }

    savingName = true
    nameError = null

    try {
      await api.rename(person.id, value)
      editingName = false
      onchanged()
    } catch (e) {
      // Keep the edit on screen so the typed name is not lost.
      nameError = e instanceof ApiError ? e.message : String(e)
    } finally {
      savingName = false
    }
  }

  /**
   * Aliases that say something the Social section does not.
   *
   * An alias identical to an account handle or display name is already listed below, so
   * repeating it under "Also known as" tells the reader nothing. In this workspace 186 of
   * 195 aliases are exactly that, which is why the section read as noise.
   */
  const meaningfulAliases = $derived.by(() => {
    const shown = new Set(
      person.accounts.flatMap((a) => [a.handle, a.displayName].filter(Boolean) as string[]),
    )

    return person.aliases.filter((alias) => !shown.has(alias))
  })

  // `onchanged` lets the parent re-fetch the detail after an edit, so the panel always shows
  // what the server actually stored rather than an optimistic local guess.
  let {
    person,
    onchanged = () => {},
    onselect = () => {},
  }: {
    person: PersonDetail
    onchanged?: () => void
    /**
     * Navigates to another contact. A friend listed here is a real person with their own
     * record, so clicking one should open it rather than force a search.
     */
    onselect?: (id: string) => void
  } = $props()
</script>

<div class="panel">
  <div class="head">
    <span class="avatar" aria-hidden="true">{initials(person.name)}</span>

    <div class="name-block">
      {#if editingName}
        <div class="name-edit">
          <input
            bind:value={nameDraft}
            aria-label="Full name"
            placeholder="First and last name"
            onkeydown={(e) => {
              if (e.key === 'Enter') saveName()
              if (e.key === 'Escape') editingName = false
            }}
          />
          <button disabled={savingName} onclick={saveName}>
            {savingName ? 'Saving…' : 'Save'}
          </button>
          <button disabled={savingName} onclick={() => (editingName = false)}>Cancel</button>
        </div>
        {#if nameError}<p class="note-error" role="alert">{nameError}</p>{/if}
      {:else}
        <h2>
          {person.name}
          <button
            class="name-edit-btn"
            title="Edit name"
            aria-label="Edit name for {person.name}"
            onclick={startEditingName}
          >✎</button>
        </h2>
      {/if}
    </div>
  </div>

  <PersonFields
    personId={person.id}
    tier={person.tier}
    tags={person.tags}
    locations={person.locations}
    preferred={person.preferred}
    howWeMet={person.howWeMet}
    gender={person.gender}
    phones={person.phones}
    emails={person.emails}
    friendsWith={person.friendsWith ?? []}
    listedBy={person.listedBy ?? []}
    knownThrough={person.knownThrough ?? null}
    metThrough={person.metThrough ?? []}
    onselect={onselect}
    onchange={onchanged}
  />

  <div class="trace">
    <div class="trace-social">
      <SocialFrame
        personId={person.id}
        accounts={person.accounts}
        personName={person.name}
        onchanged={onchanged}
      />
    </div>
  </div>

  {#if person.accounts.length > 0}
    <section>
      <h3>Accounts</h3>
      <ul class="plain">
        {#each person.accounts as account (account.platform + account.handle)}
          <li class="account">
            <span class="chip" style="--hue: {platformHue(account.platform)}">
              {platformLabel(account.platform)}
            </span>

            {#if account.profileUrl}
              <a href={account.profileUrl} target="_blank" rel="noreferrer">
                {account.handle}
              </a>
            {:else}
              <span>{account.handle}</span>
            {/if}
          </li>
        {/each}
      </ul>
    </section>
  {/if}

  <section>
    <h3>Kind</h3>
    <select
      class="kind-select"
      value={person.kind}
      aria-label="Contact kind"
      onchange={(e) => {
        api.setKind(person.id, (e.target as HTMLSelectElement).value).then(onchanged)
      }}
    >
      <option value="person">Person</option>
      <option value="business">Business</option>
      <option value="place">Place</option>
      <option value="organisation">Organisation</option>
    </select>
    {#if person.kind !== 'person'}
      <p class="muted hint-line">Excluded from guest lists and the friend network.</p>
    {/if}
  </section>

  <section class="not-a-contact">
    <button
      class="link-toggle"
      class:on={person.isMe}
      onclick={async () => {
        await api.setIsMe(person.id, !person.isMe)
        onchanged()
      }}
    >
      {person.isMe ? 'This is you' : 'This is me'}
    </button>
    <span class="muted hint-line">
      {person.isMe
        ? 'You are at the centre of the network graph. Marking someone else clears this.'
        : 'Puts this contact at the centre of the network graph instead of a placeholder.'}
    </span>
  </section>

  <section class="not-a-contact">
    <button
      class="link-toggle"
      class:on={person.notAContact}
      onclick={async () => {
        await api.setNotAContact(person.id, !person.notAContact)
        onchanged()
      }}
    >
      {person.notAContact ? 'Marked as not a contact' : 'Not a contact'}
    </button>
    <span class="muted hint-line">
      {person.notAContact
        ? 'Hidden from lists and counts. The record is kept, so a re-import will not bring it back as a contact.'
        : 'Keeps the record but hides this person from lists, counts and the graph.'}
    </span>
  </section>

  <section class="delete-person">
    <!-- Delete is separated from "not a contact" on purpose: one hides a record, the other
         destroys it. Two steps, and the button names the person, because the panel is narrow
         and the wrong row is one click away. -->
    {#if confirmingDelete}
      <p class="delete-warning" role="alert">
        Delete {person.name}? This removes the record and the links to it in other contacts, and
        a re-import will not bring them back.
      </p>
      <div class="delete-actions">
        <button class="danger" disabled={deleting} onclick={confirmDelete}>
          {deleting ? 'Deleting…' : 'Yes, delete'}
        </button>
        <button disabled={deleting} onclick={() => (confirmingDelete = false)}>Cancel</button>
      </div>
    {:else}
      <button class="link-toggle danger-text" onclick={() => (confirmingDelete = true)}>
        Delete contact
      </button>
      <span class="muted hint-line">
        Removes the record permanently. Use "Not a contact" to keep it but hide it.
      </span>
    {/if}

    {#if deleteError}
      <p class="note-error" role="alert">{deleteError}</p>
    {/if}
  </section>

  {#if meaningfulAliases.length > 0}
    <section>
      <h3>Also known as</h3>
      <ul class="plain">
        {#each meaningfulAliases as alias (alias)}
          <li>{alias}</li>
        {/each}
      </ul>
    </section>
  {/if}

  <MergeBox
    personId={person.id}
    personName={person.name}
    sources={person.sources}
    onmerged={onchanged}
  />

  {#if person.sources.length > 1}
    <section>
      <h3>Matched across</h3>
      <ul class="plain">
        {#each person.sources as source (source)}
          <li>{platformLabel(source)}</li>
        {/each}
      </ul>
    </section>
  {/if}
</div>