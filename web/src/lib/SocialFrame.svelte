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

  let activeIndex = $state(0)
  let frameKey = $state(0)
  let frameState = $state<'loading' | 'loaded' | 'blocked'>('loading')
  let timer: ReturnType<typeof setTimeout> | undefined

  function profileUrl(account: AccountSummary): string | null {
    if (account.profileUrl) {
      // A few export rows carry a slug ending in a hyphen, which is a 404.
      return account.profileUrl.replace(/[-/]+$/, '')
    }

    const handle = account.handle.replace(/^@/, '')

    if (!handle || /\s/.test(handle)) {
      return null
    }

    switch (account.platform.toLowerCase()) {
      case 'instagram':
        return `https://www.instagram.com/${handle}/`
      case 'linkedin':
        return /^[a-z0-9][a-z0-9-]*[a-z0-9]$/i.test(handle)
          ? `https://www.linkedin.com/in/${handle}/`
          : null
      case 'mastodon':
        return handle.includes('@')
          ? `https://${handle.split('@').pop()}/@${handle.split('@')[0]}`
          : null;
      case 'bluesky':
        return `https://bsky.app/profile/${handle}`
      case 'facebook':
        return `https://www.facebook.com/${handle}`
      default:
        return null
    }
  }

  const active = $derived(accounts[activeIndex])
  const url = $derived(active ? profileUrl(active) : null)

  // Verified against the live sites: every platform this app ingests refuses framing.
  const SILENT_BLOCKERS = new Set(['instagram', 'facebook', 'linkedin', 'mastodon', 'bluesky', 'telegram'])

  const knownBlocked = $derived(
    active !== undefined && SILENT_BLOCKERS.has(active.platform.toLowerCase()),
  )

  function selectAccount(index: number) {
    activeIndex = index
    startTimer()
  }

  function startTimer() {
    clearTimeout(timer)
    frameState = 'loading'
    frameKey += 1

    if (!knownBlocked) {
      timer = setTimeout(() => {
        if (frameState === 'loading') {
          frameState = 'blocked'
        }
      }, 5000)
    }
  }

  function onLoad() {
    clearTimeout(timer)
    frameState = knownBlocked ? 'blocked' : 'loaded'
  }

  $effect(() => {
    if (knownBlocked) {
      clearTimeout(timer)
      frameState = 'blocked'
    }
  })

  let lastAccounts: unknown = $state(null)
  $effect(() => {
    if (accounts === lastAccounts) {
      return
    }

    lastAccounts = accounts
    activeIndex = 0
    startTimer()
  })

  $effect(() => () => clearTimeout(timer))
</script>

<section class="socials">
  <h3>Social</h3>

  {#if accounts.length === 0}
    <p class="muted">No social accounts recorded.</p>
  {:else}
    <div class="social-tabs" role="tablist" aria-label="Social accounts">
      {#each accounts as account, index (account.platform + account.handle)}
        <button
          role="tab"
          aria-selected={index === activeIndex}
          class:active={index === activeIndex}
          onclick={() => selectAccount(index)}
        >
          {platformLabel(account.platform)}
        </button>
      {/each}
    </div>

    {#if url}
      <!-- Always available, whatever the iframe does. -->
      <a class="social-open" href={url} target="_blank" rel="noreferrer noopener">
        Open {platformLabel(active.platform)} profile ↗
      </a>

      <div class="social-frame">
        {#if frameState === 'blocked' || knownBlocked}
          <!-- The common case: the platform sends X-Frame-Options. Say so plainly and give
               the real actions, rather than leaving an empty box. -->
          <div class="social-blocked">
            <p>
              <strong>{platformLabel(active.platform)}</strong> does not allow being shown
              inside another app.
            </p>
            <p class="muted">
              This is the platform's choice, not a fault here. Open the profile in a tab and
              keep this panel's details and notes beside it.
            </p>
            <div class="social-actions">
              <a href={url} target="_blank" rel="noreferrer noopener">Open in a new tab ↗</a>
            </div>
          </div>
        {:else}
          {#key frameKey}
            <iframe
              src={url}
              title="{personName} on {platformLabel(active.platform)}"
              onload={onLoad}
              referrerpolicy="no-referrer"
              sandbox="allow-scripts allow-same-origin allow-popups allow-forms"
              loading="lazy"
            ></iframe>
          {/key}

          {#if frameState === 'loading'}
            <p class="social-loading muted">Loading {platformLabel(active.platform)}…</p>
          {/if}
        {/if}
      </div>
    {:else}
      <p class="muted">
        No profile URL for this account. The handle is
        <code>{active.handle}</code> — add one with a profile link if you know it.
      </p>
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

  {#if accounts.length > 0}
    <ul class="account-list" aria-label="Accounts that can be removed">
      {#each accounts as account (account.platform + account.handle)}
        <li>
          <span class="chip" style="--hue: {platformHue(account.platform)}">
            {platformLabel(account.platform)}
          </span>
          <span class="account-handle">{account.handle}</span>
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
</section>
