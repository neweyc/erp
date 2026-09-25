/** Today in UTC, as the server counts it — the close rule compares against the server's today. */
export const today = (): string => new Date().toISOString().slice(0, 10)
