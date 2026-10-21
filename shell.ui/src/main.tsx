import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
// Served from this origin, not a font CDN: no third party learns who opens the app, and nothing
// breaks when that CDN is unreachable.
import '@fontsource-variable/inter'
import './index.css'
import { App } from './App'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
