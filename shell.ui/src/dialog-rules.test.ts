// @vitest-environment node
import { readdirSync, readFileSync, statSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

/**
 * shell.ui/CLAUDE.md: a dialog holding a part-filled form passes `dismissible={false}`, because a
 * stray click on the backdrop otherwise discards what was typed with no undo. The kit's Dialog
 * makes `dismissible` a required prop, so every dialog decides; this checks the decision for every
 * dialog with a form in the shell and in every app, including ones added later.
 *
 * (The other half of the rule, height capped in `svh`, lives in the kit's Dialog itself and is
 * tested there: packages/ui-kit/src/dialog.test.tsx.)
 */
const root = fileURLToPath(new URL('../../', import.meta.url))
const sources = [join(root, 'shell.ui/src'), join(root, 'apps')]

function sourceFiles(directory: string): string[] {
  return readdirSync(directory).flatMap((name) => {
    const path = join(directory, name)
    if (name === 'node_modules' || name === 'dist') return []
    if (statSync(path).isDirectory()) return sourceFiles(path)
    return /\.tsx$/.test(name) && !/\.test\.tsx$/.test(name) ? [path] : []
  })
}

/** Each `<Dialog …>…</Dialog>` element in a file, as text. ConfirmDialog holds no form and is not matched. */
function dialogs(source: string): string[] {
  return [...source.matchAll(/<Dialog\b[\s\S]*?<\/Dialog>/g)].map((m) => m[0])
}

describe('form dialogs', () => {
  const found = sources.flatMap(sourceFiles).flatMap((file) =>
    dialogs(readFileSync(file, 'utf8')).map((text) => ({ file: file.slice(root.length), text })),
  )

  it('are found at all (a scan that matches nothing proves nothing)', () => {
    expect(found.filter((d) => d.text.includes('<form')).length).toBeGreaterThan(0)
  })

  it('are never dismissible by a stray click or Escape', () => {
    const offenders = found
      .filter((d) => d.text.includes('<form') && !/dismissible=\{false\}/.test(d.text))
      .map((d) => d.file)

    expect(offenders).toEqual([])
  })
})
