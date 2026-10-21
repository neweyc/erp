/**
 * The shared component kit: every page in the shell and every app builds from these, so the
 * product looks like one product. Styling comes from Tailwind classes resolved against the tokens
 * in theme.css; the shell's stylesheet imports that file and scans this package for classes.
 */
export { cn } from './cn'
export { Button, buttonVariants } from './button'
export { Input, NativeSelect, Checkbox } from './input'
export { Label, Field, CheckboxRow } from './field'
export { Card, CardHeader, CardContent, CardFooter } from './card'
export { Badge, type BadgeTone } from './badge'
export { Alert } from './alert'
export { Table, TableHeader, TableBody, TableFooter, TableRow, TableHead, TableCell, numeric } from './table'
export { DataTable, type ColumnDef, type ColumnMeta } from './data-table'
export { Dialog, DialogBody, DialogFooter, ConfirmDialog, Sheet } from './dialog'
export {
  Menu, MenuTrigger, MenuContent, MenuItem, MenuLabel, MenuSeparator, MenuRadioGroup, MenuRadioItem,
} from './menu'
export { PageHeader, PageStack, EmptyState, Loading, Avatar, initials } from './page'
