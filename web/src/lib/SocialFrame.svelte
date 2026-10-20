<script lang="ts">
  import type { AccountSummary } from './api'
  import { platformLabel } from './format'

  let {
    accounts,
    personName,
  }: { accounts: AccountSummary[]; personName: string } = $props()

  let activeIndex = $state(0)
  let frameKey = $state(0)
  // 'loading' until the iframe reports load; if it never does within the timeout, we assume
  // it was blocked.
  let frameState = $state<'loading' | 'loaded' | 'blocked'>('loading')
  let timer: ReturnType<typeof setTimeout> | undefined

  /**
   * Builds a profile URL from an account.
   *
   * Prefers the URL the export gave us. Falls back to constructing one from the handle,
   * because several sources (Instagram followers, Facebook friends) carry a handle but no
   * URL, and a guessed URL is better than no link at all.
   */
  function profileUrl(account: AccountSummary): string | null {
    if (account.profileUrl) {
      // Two LinkedIn export rows in this workspace carry a slug ending in a hyphen
      // (".../in/abdullah-dabbagh-"), which is a 404. Trim trailing hyphens from the path
      // rather than showing a broken link.
      return account.profileUrl.replace(/[-/]+$/, '')
    }

    // A merged person can carry a handle from one platform alongside an account from
    // another. Constructing a URL from a foreign handle produces a 404, so require the
    // handle to be plausible for this platform first.

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
          : null;
      case 'mastodon':
        return handle.includes('@') ? `https://${handle.split('@').pop()}/@${handle.split('@')[0]}` : null
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

  /**
   * Whether this platform is known to refuse framing.
   *
   * Verified against the live sites: Instagram and Facebook send `X-Frame-Options: DENY`,
   * LinkedIn sends `frame-ancestors 'self'`, and Mastodon sends `frame-ancestors 'none'`. For
   * every platform this app ingests, the honest default is "this will not render".
   *
   * Detection by timeout does not work. The browser fires `load` for a refused frame too, and
   * the cross-origin document cannot be inspected, so the timeout never fires and the user is
   * left with an empty box. Knowing the answer in advance is more reliable and faster than
   * waiting to discover it.
   */
  // Bluesky sends X-Frame-Options: SAMEORIGIN and renders blank in practice, so it belongs
  // here too. Every platform this app ingests refuses framing; the set is kept as a set
  // rather than a blanket "always blocked" so a future platform that does embed (a small
  // self-hosted instance, say) still gets a real attempt.
  const SILENT_BLOCKERS = new Set([
    'instagram',
    'facebook',
    'linkedin',
    'mastodon',
    'bluesky',
    'telegram',
  ])

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

    // Only arm the timeout for platforms that might render. For known blockers the notice is
    // shown immediately, so there is nothing to wait for.
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

    // A load event does not prove the content rendered - a refused frame fires it too - so
    // it only clears the spinner for platforms we have no prior on.
    frameState = knownBlocked ? 'blocked' : 'loaded'
  }

  // The notice must also appear when the platform changes without a remount.
  $effect(() => {
    if (knownBlocked) {
      clearTimeout(timer)
      frameState = 'blocked'
    }
  })

  // Restart when the PERSON changes. Tracking only `accounts` keeps the effect from
  // re-running on the state it writes (activeIndex, frameState), which is what produced
  // Svelte's effect_update_depth_exceeded.
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
</section>
