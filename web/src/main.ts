// Self-hosted variable fonts. Bundled rather than loaded from a CDN: this is a local
// app that must work offline, and a font request is an unnecessary third-party leak.
import '@fontsource-variable/montserrat'
import '@fontsource-variable/manrope'
import { mount } from 'svelte'
import './app.css'
import App from './App.svelte'

const app = mount(App, {
  target: document.getElementById('app')!,
})

export default app
