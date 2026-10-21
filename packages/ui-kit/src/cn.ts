import { clsx, type ClassValue } from 'clsx'
import { twMerge } from 'tailwind-merge'

/**
 * Joins class names, letting a later Tailwind class override an earlier one of the same kind:
 * `cn('px-4', 'px-2')` is `px-2`, so a caller's `className` beats a component's default.
 */
export function cn(...inputs: ClassValue[]): string {
  return twMerge(clsx(inputs))
}
