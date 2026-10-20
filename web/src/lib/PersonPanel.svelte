<script lang="ts">
  import type { PersonDetail } from './api'
  import { initials, platformHue, platformLabel } from './format'

  let { person }: { person: PersonDetail } = $props()
</script>

<div class="panel">
  <div class="head">
    <span class="avatar" aria-hidden="true">{initials(person.name)}</span>

    <div>
      <h2>{person.name}</h2>

      {#if person.tierLabel !== '-'}
        <span class="tier-pill">{person.tierLabel}</span>
      {/if}
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

  {#if person.locations.length > 0}
    <section>
      <h3>Locations</h3>
      <ul class="plain">
        {#each person.locations as location (location.place + location.kind)}
          <li>
            {location.place}
            {#if location.kind !== 'other'}
              <span class="reason">({location.kind})</span>
            {/if}
            {#if location.note}
              <span class="reason">— {location.note}</span>
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

  {#if person.notes.length > 0}
    <section>
      <h3>Notes</h3>
      {#each person.notes as note (note.fileName)}
        <div class="note">{note.body}</div>
      {/each}
    </section>
  {/if}

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