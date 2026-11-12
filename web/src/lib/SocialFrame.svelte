<script lang="ts">
  import { api, ApiError, type AccountSummary } from './api'
  import { hasProfileConcept, platformHue, platformLabel } from './format'

  let {
    personId,
    accounts,
    personName,
    onchanged = () => {},
  }: {
    personId: string
    accounts: AccountSummary[]
    personName: string
    onchanged?: () => void
  } = $props()

  /**
   * Platforms offered for hand-entry, social networks first.
   *
   * The list used to be only the messaging services, because the social platforms were
   * assumed to arrive from an export. They often do not: an account found by talking to
   * someone, or a second handle for a person whose export predates it, had no way in.
   *
   * Facebook is deliberately absent; it never yields a profile URL from a dump, so adding one
   * by hand invents a link that cannot be verified. Mastodon and Bluesky are not offered here
   * either - platforms are added to this list when they will be used, not because they exist.
   * An account on either can still be recorded through "Something else", and existing records
   * that already carry one are untouched.
   */
  const ADDABLE = [
    { value: 'instagram', label: 'Instagram' },
    { value: 'linkedin', label: 'LinkedIn' },
    { value: 'discord', label: 'Discord' },
    { value: 'twitter', label: 'Twitter (X)' },
    { value: 'signal', label: 'Signal' },
    { value: 'whatsapp', label: 'WhatsApp' },
    { value: 'telegram', label: 'Telegram' },
    { value: 'sms', label: 'SMS' },
    { value: 'other', label: 'Something else' },
  ]

  let adding = $state(false)
  let newPlatform = $state('instagram')
  let customPlatform = $state('')
  let newHandle = $state('')
  let busy = $state(false)
  let addError = $state<string | null>(null)

  async function addAccount() {
    const platform = newPlatform === 'other' ? customPlatform.trim().toLowerCase() : newPlatform
    const handle = newHandle.trim()

    if (!platform || !handle) {
      addError = 'Enter both a platform and a handle.'
      return
    }

    busy = true
    addError = null

    try {
      await api.addAccount(personId, platform, handle)
      adding = false
      newHandle = ''
      customPlatform = ''
      onchanged()
    } catch (e) {
      // Keep the typed handle on screen so it is not lost to a failed request.
      addError = e instanceof ApiError ? e.message : String(e)
    } finally {
      busy = false
    }
  }

  async function removeAccount(platform: string, handle: string) {
    busy = true
    addError = null

    try {
      await api.removeAccount(personId, platform, handle)
      onchanged()
    } catch (e) {
      addError = e instanceof ApiError ? e.message : String(e)
    } finally {
      busy = false
    }
  }

  /**
   * Builds a profile URL from an account.
   *
   * Prefers the URL the export gave us. Falls back to constructing one from the handle,
   * because several sources (Instagram followers, Facebook friends) carry a handle but no
   * URL, and a guessed URL is better than no link at all.
   */
  function profileUrl(account: AccountSummary): string | null {
    if (account.profileUrl) {
      // A few LinkedIn export rows carry a slug ending in a hyphen, which is a 404.
      return account.profileUrl.replace(/[-/]+$/, '')
    }

    const handle = account.handle.replace(/^@/, '')

    if (!handle || /\s/.test(handle)) {
      // A display name is not a handle; do not invent a URL that will 404.
      return null
    }

    switch (account.platform.toLowerCase()) {
      case 'instagram':
        return `https://www.instagram.com/${handle}/`
      case 'linkedin':
        // A LinkedIn handle is a slug; anything else (a trailing hyphen, punctuation) came
        // from a different platform's handle and would produce a 404.
        return /^[a-z0-9][a-z0-9-]*[a-z0-9]$/i.test(handle)
          ? `https://www.linkedin.com/in/${handle}/`
          : null
      case 'mastodon':
        return handle.includes('@')
          ? `https://${handle.split('@').pop()}/@${handle.split('@')[0]}`
          : null
      case 'bluesky':
        return `https://bsky.app/profile/${handle}`
      case 'facebook':
        return `https://www.facebook.com/${handle}`
      default:
        return null
    }
  }

  /**
   * Accounts that are a presence on a social network, split by whether a link can be built.
   *
   * Contact platforms (Contacts, Signal, WhatsApp) are excluded here entirely: they are ways
   * to reach someone, not places they have a page, so "no profile URL" was a complaint about
   * an absence that could never have been filled.
   */
  const social = $derived(
    accounts
      .filter((account) => hasProfileConcept(account.platform))
      .map((account) => ({ account, url: profileUrl(account) })),
  )

  /** Social accounts that resolve to a link. */
  const linked = $derived(
    social.filter(
      (entry): entry is { account: AccountSummary; url: string } => entry.url !== null,
    ),
  )

  /** Social accounts with no derivable link: the handle is all there is. */
  const unlinked = $derived(social.filter((entry) => entry.url === null))

  /** Contact platforms: phone numbers and addresses, never links. */
  const contactMethods = $derived(
    accounts.filter((account) => !hasProfileConcept(account.platform)),
  )
</script>

<section class="socials">
  <h3>Social</h3>

  {#if social.length === 0 && contactMethods.length === 0}
    <p class="muted">No social accounts recorded.</p>
  {:else}
    <!-- External links only. Embedding was tried and abandoned: every platform this app
         ingests refuses to be framed (Instagram and Facebook send X-Frame-Options: DENY,
         LinkedIn and Mastodon restrict frame-ancestors), so an iframe showed a blank box for
         every real contact. Detecting that reliably is impossible from the parent page - the
         browser fires load for a refused frame too - so the feature was a framed blank with
         an apology underneath. A plain link is honest and always works. -->
    {#if linked.length > 0}
      <ul class="social-links">
        {#each linked as entry (entry.account.platform + entry.account.handle)}
          <li>
            <a href={entry.url} target="_blank" rel="noreferrer noopener">
              <span class="chip" style="--hue: {platformHue(entry.account.platform)}">
                {platformLabel(entry.account.platform)}
              </span>
              <span class="social-handle">{entry.account.handle}</span>
              <span class="social-arrow" aria-hidden="true">↗</span>
            </a>

            <!-- The remove control lives on the row it removes. A separate repeated list was
                 the obvious alternative and doubled the height of the section for no gain. -->
            <button
              class="account-remove"
              title="Remove {entry.account.handle}"
              aria-label="Remove {entry.account.platform} account {entry.account.handle}"
              disabled={busy}
              onclick={() => removeAccount(entry.account.platform, entry.account.handle)}
            >×</button>
          </li>
        {/each}
      </ul>
    {/if}

    {#if unlinked.length > 0}
      <!-- A social handle we cannot turn into a link. The handle is still the identifier, so
           it is shown; it is not labelled a defect. -->
      <ul class="social-links social-unlinked">
        {#each unlinked as entry (entry.account.platform + entry.account.handle)}
          <li>
            <span class="chip" style="--hue: {platformHue(entry.account.platform)}">
              {platformLabel(entry.account.platform)}
            </span>
            <span class="social-handle">{entry.account.handle}</span>
            <button
              class="account-remove"
              title="Remove {entry.account.handle}"
              aria-label="Remove {entry.account.platform} account {entry.account.handle}"
              disabled={busy}
              onclick={() => removeAccount(entry.account.platform, entry.account.handle)}
            >×</button>
          </li>
        {/each}
      </ul>
    {/if}

    {#if contactMethods.length > 0}
      <!-- Ways to reach someone. These have no profile by definition, so there is nothing
           to link to and nothing missing. -->
      <h4 class="social-subhead">Contact methods</h4>
      <ul class="social-links social-unlinked">
        {#each contactMethods as account (account.platform + account.handle)}
          <li>
            <span class="chip" style="--hue: {platformHue(account.platform)}">
              {platformLabel(account.platform)}
            </span>
            <span class="social-handle">{account.handle}</span>
            <button
              class="account-remove"
              title="Remove {account.handle}"
              aria-label="Remove {account.platform} account {account.handle}"
              disabled={busy}
              onclick={() => removeAccount(account.platform, account.handle)}
            >×</button>
          </li>
        {/each}
      </ul>
    {/if}
  {/if}

  {#if addError}
    <p class="note-error" role="alert">{addError}</p>
  {/if}

  {#if adding}
    <div class="inline-add account-add">
      <select bind:value={newPlatform} aria-label="Platform">
        {#each ADDABLE as option (option.value)}
          <option value={option.value}>{option.label}</option>
        {/each}
      </select>

      {#if newPlatform === 'other'}
        <input
          bind:value={customPlatform}
          placeholder="Platform name"
          aria-label="Platform name"
        />
      {/if}

      <input
        bind:value={newHandle}
        placeholder={['signal', 'whatsapp', 'sms'].includes(newPlatform)
          ? '+1 555 0100 or username'
          : 'Handle'}
        aria-label="Handle"
        onkeydown={(e) => { if (e.key === 'Enter') addAccount() }}
      />

      <button disabled={busy} onclick={addAccount}>{busy ? 'Adding…' : 'Add'}</button>
      <button disabled={busy} onclick={() => { adding = false; addError = null }}>Cancel</button>
    </div>
  {:else}
    <button class="account-add-btn" onclick={() => (adding = true)}>+ Add account</button>
  {/if}

</section>
