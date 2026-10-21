<script lang="ts">
  import { api, ApiError, type PersonDetail } from './api'
  import { initials, platformHue, platformLabel } from './format'
  import PersonFields from './PersonFields.svelte'
  import MergeBox from './MergeBox.svelte'
  import SocialFrame from './SocialFrame.svelte'

  // Inline name editing. Instagram contacts arrive named after their handle, and there was
  // no way to give them a real first and last name without dropping to the CLI.
  let editingName = $state(false)
  let nameDraft = $state('')
  let savingName = $state(false)
  let nameError = $state<string | null>(null)

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
    friendsWith={person.friendsWith ?? []}
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

  {#if person.phones.length > 0}
    <section>
      <h3>Phone</h3>
      <ul class="plain">
        {#each person.phones as phone (phone)}
          <li><a href="tel:{phone.replace(/[^+\d]/g, '')}">{phone}</a></li>
        {/each}
      </ul>
    </section>
  {/if}

  {#if person.emails.length > 0}
    <section>
      <h3>Email</h3>
      <ul class="plain">
        {#each person.emails as email (email)}
          <li><a href="mailto:{email}">{email}</a></li>
        {/each}
      </ul>
    </section>
  {/if}

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