import { stopDatabase } from './stack.mjs'

export default function globalTeardown() {
  stopDatabase()
}
