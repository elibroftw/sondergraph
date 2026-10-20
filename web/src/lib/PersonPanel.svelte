<script lang="ts">
  import type { PersonDetail } from './api'
  import { initials, platformHue, platformLabel } from './format'
  import NoteEditor from './NoteEditor.svelte'
  import PersonFields from './PersonFields.svelte'
  import MergeBox from './MergeBox.svelte'

  // `onchanged` lets the parent re-fetch the detail after an edit, so the panel always shows
  // what the server actually stored rather than an optimistic local guess.
  let { person, onchanged = () => {} }: { person: PersonDetail; onchanged?: () => void } = $props()
</script>

<div class="panel">
  <div class="head">
    <span class="avatar" aria-hidden="true">{initials(person.name)}</span>

    <div>
      <h2>{person.name}</h2>


    </div>
  </div>

  <PersonFields
    personId={person.id}
    tier={person.tier}
    tags={person.tags}
    locations={person.locations}
    onchange={onchanged}
  />

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

  {#if person.aliases.length > 0}
    <section>
      <h3>Also known as</h3>
      <ul class="plain">
        {#each person.aliases as alias (alias)}
          <li>{alias}</li>
        {/each}
      </ul>
    </section>
  {/if}

  <NoteEditor personId={person.id} personName={person.name} />

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