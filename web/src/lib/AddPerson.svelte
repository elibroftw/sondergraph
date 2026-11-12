<script lang="ts">
  import { api, ApiError } from './api'

  let {
    oncreated = () => {},
    oncancel = () => {},
  }: {
    oncreated?: (id: string) => void
    oncancel?: () => void
  } = $props()

  // Platforms offered by name. The list is short on purpose: these are the ones people
  // actually type into a contact card. Anything else goes through "Something else".
  const PLATFORM_CHOICES = [
    { value: 'instagram', label: 'Instagram' },
    { value: 'linkedin', label: 'LinkedIn' },
    { value: 'discord', label: 'Discord' },
    { value: 'twitter', label: 'Twitter' },
    { value: 'signal', label: 'Signal' },
    { value: 'whatsapp', label: 'WhatsApp' },
    { value: 'telegram', label: 'Telegram' },
    { value: 'facebook', label: 'Facebook' },
    { value: 'bluesky', label: 'Bluesky' },
    { value: 'mastodon', label: 'Mastodon' },
  ] as const

  let name = $state('')
  let phone = $state('')
  let email = $state('')
  let platform = $state<string>('instagram')
  let handle = $state('')
  let customPlatform = $state('')
  let busy = $state(false)
  let error = $state<string | null>(null)
  let dialogEl = $state<HTMLDialogElement | null>(null)

  $effect(() => {
    // showModal, not show(): only the modal form gives the focus trap and Escape handling
    // that make this behave like a dialog rather than a floating box.
    dialogEl?.showModal()
  })

  function close() {
    dialogEl?.close()
    oncancel()
  }

  async function submit(event: SubmitEvent) {
    event.preventDefault()

    if (!name.trim()) {
      error = 'A contact needs a name.'
      return
    }

    // Only send an account when both halves are filled in. A half-filled row is not an
    // error worth blocking on - the user may simply have wanted a phone number.
    const accounts =
      handle.trim() && (platform !== 'other' || customPlatform.trim())
        ? [
            {
              platform: platform === 'other' ? customPlatform.trim().toLowerCase() : platform,
              handle: handle.trim(),
            },
          ]
        : []

    busy = true
    error = null

    try {
      const person = await api.createPerson({
        name: name.trim(),
        phones: phone.trim() ? [phone.trim()] : [],
        emails: email.trim() ? [email.trim()] : [],
        accounts,
      })

      oncreated(person.id)
    } catch (e) {
      // Keep what was typed on screen: re-entering a whole contact because a request failed
      // is worse than the failure.
      error = e instanceof ApiError ? e.message : String(e)
    } finally {
      busy = false
    }
  }
</script>

<!-- A native <dialog> with showModal rather than a styled div: it traps focus, closes on
     Escape, and is announced as a dialog without hand-written aria bookkeeping. The previous
     version put role="dialog" on a <form> inside a clickable overlay, which typed fine and
     read badly to a screen reader. -->
<dialog bind:this={dialogEl} class="add-person" aria-labelledby="add-person-title">
  <form onsubmit={submit}>
    <h2 id="add-person-title">Add a contact</h2>
    <p class="muted hint-line">
      Details are optional. Only the name is required, and everything can be filled in later
      on the contact's page.
    </p>

    <label class="field">
      <span>Name</span>
      <input bind:value={name} placeholder="Ada Lovelace" required />
    </label>

    <div class="field-row">
      <label class="field">
        <span>Phone</span>
        <input bind:value={phone} placeholder="+1 555 0100" inputmode="tel" />
      </label>

      <label class="field">
        <span>Email</span>
        <input bind:value={email} placeholder="ada@example.com" inputmode="email" />
      </label>
    </div>

    <fieldset class="social-fieldset">
      <legend>Social account</legend>

      <div class="field-row">
        <label class="field">
          <span>Platform</span>
          <select bind:value={platform} aria-label="Platform">
            {#each PLATFORM_CHOICES as option (option.value)}
              <option value={option.value}>{option.label}</option>
            {/each}
            <option value="other">Something else</option>
          </select>
        </label>

        {#if platform === 'other'}
          <label class="field">
            <span>Platform name</span>
            <input bind:value={customPlatform} placeholder="Mastodon instance, forum…" />
          </label>
        {/if}

        <label class="field">
          <span>Handle</span>
          <input bind:value={handle} placeholder="ada" />
        </label>
      </div>
    </fieldset>

    {#if error}
      <p class="note-error" role="alert">{error}</p>
    {/if}

    <div class="add-person-actions">
      <button type="button" onclick={close} disabled={busy}>Cancel</button>
      <button type="submit" class="primary" disabled={busy}>
        {busy ? 'Adding…' : 'Add contact'}
      </button>
    </div>
  </form>
</dialog>

<style>
  /* A native dialog is centred by the browser and sits in the top layer, so no backdrop
     element or z-index juggling is needed. The ::backdrop pseudo-element styles the scrim. */
  .add-person {
    width: min(560px, calc(100vw - 32px));
    margin: 8vh auto auto;
    padding: 22px;
    border: 1px solid var(--border);
    border-radius: 14px;
    background: var(--panel);
    color: var(--text);
    box-shadow: 0 24px 60px rgba(3, 8, 20, 0.45);
  }

  .add-person::backdrop {
    background: rgba(8, 12, 22, 0.55);
    backdrop-filter: blur(2px);
  }

  .add-person form {
    display: flex;
    flex-direction: column;
    gap: 14px;
  }

  .add-person h2 {
    margin: 0;
    font-family: var(--font-display);
    font-size: 18px;
    font-weight: 600;
  }

  .field {
    display: flex;
    flex-direction: column;
    gap: 5px;
    flex: 1 1 0;
    min-width: 0;
  }

  /* The global stylesheet only sets a font on controls, so without this the fields render as
     browser-default white boxes on the dark surface. */
  .add-person input,
  .add-person select {
    width: 100%;
    padding: 8px 10px;
    border: 1px solid var(--border);
    border-radius: 8px;
    background: var(--panel-raised);
    color: inherit;
    font-size: 14px;
  }

  .add-person input::placeholder {
    color: var(--muted);
    opacity: 0.7;
  }

  .add-person input:focus,
  .add-person select:focus {
    outline: none;
    border-color: var(--accent);
    box-shadow: 0 0 0 3px var(--accent-soft);
  }

  .field > span {
    color: var(--muted);
    font-family: var(--font-mono);
    font-size: 11px;
    letter-spacing: 0.08em;
    text-transform: uppercase;
  }

  .field-row {
    display: flex;
    gap: 12px;
    flex-wrap: wrap;
  }

  .social-fieldset {
    margin: 0;
    padding: 12px;
    border: 1px solid var(--border);
    border-radius: 10px;
  }

  .social-fieldset legend {
    padding: 0 6px;
    color: var(--muted);
    font-family: var(--font-mono);
    font-size: 11px;
    letter-spacing: 0.08em;
    text-transform: uppercase;
  }

  .add-person-actions {
    display: flex;
    justify-content: flex-end;
    gap: 10px;
    margin-top: 4px;
  }
</style>
