# Supported version combinations

Independent deploy cadences produce a grid: every deployment sits on some combination of
shell, core, platform, and app versions. A monorepo makes it look like there is one
version of the world. There is not.

Fill this in from the first tagged release. Until then the only supported combination is
"everything from the same commit".

| Shell | core.api | platform.api | tickets.api | Status | Notes |
|---|---|---|---|---|---|
| — | — | — | — | — | No tagged releases yet |

## Policy (to be set)

- How many minor versions back an API supports a shell.
- How long a deprecated route version survives after its successor ships.
- What happens when a tenant declines an app upgrade.

Recorded as open in `open-questions.md`.
