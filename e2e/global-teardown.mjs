import { RESTORE_CONTAINER, stopDatabase } from './stack.mjs'

export default function globalTeardown() {
  stopDatabase()
  // Normally removed by the restore drill itself; here too in case that spec was interrupted.
  stopDatabase(RESTORE_CONTAINER)
}
