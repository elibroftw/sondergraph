<script lang="ts">
  import { api, ApiError, type AccountSummary } from './api'
  import { platformHue, platformLabel } from './format'

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

  // Platforms offered for hand-entry. Signal has no contact export at all, so this is the
  // only way a Signal number or username can ever be recorded.
  const ADDABLE = [
    { value: 'signal', label: 'Signal' },
    { value: 'whatsapp', label: 'WhatsApp' },
    { value: 'telegram', label: 'Telegram' },
    { value: 'mastodon', label: 'Mastodon' },
    { value: 'bluesky', label: 'Bluesky' },
    { value: 'other', label: 'Something else' },
  ]

  let adding = $state(false)
  let newPlatform = $state('signal')
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

  /** Accounts that resolve to a URL, in the order the person's accounts were recorded. */
  const linked = $derived(
    accounts
      .map((account) => ({ account, url: profileUrl(account) }))
      .filter((entry): entry is { account: AccountSummary; url: string } => entry.url !== null),
  )

  /** Accounts with no usable URL: still worth showing, since the handle is the identifier. */
  const unlinked = $derived(
    accounts.filter((account) => profileUrl(account) === null),
  )
</script>

<section class="socials">
  <h3>Social</h3>

  {#if accounts.length === 0}
    <p class="muted">No social accounts recorded.</p>
  {:else}
    <!-- External links only. Embedding was tried and abandoned: every platform this app
         ingests refuses to be framed (Instagram and Facebook send X-Frame-Options: DENY,
         LinkedIn and Mastodon restrict frame-ancestors), so an iframe showed a blank box for
         every real contact. Detecting that reliably is impossible from the parent page - the
         browser fires load for a refused frame too - so the feature was a framed blank with
         an apology underneath. A plain link is honest and always works. -->
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

    {#if unlinked.length > 0}
      <ul class="social-links social-unlinked">
        {#each unlinked as account (account.platform + account.handle)}
          <li>
            <span class="chip" style="--hue: {platformHue(account.platform)}">
              {platformLabel(account.platform)}
            </span>
            <span class="social-handle">{account.handle}</span>
            <span class="muted social-note">no profile URL</span>
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
        placeholder={newPlatform === 'signal' ? '+1 555 0100 or username' : 'Handle or number'}
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
