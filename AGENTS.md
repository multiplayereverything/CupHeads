# CupHeads development

CupHeads was made by Germanized; this fork is maintained by Multiplayer Everything. Start with `README.md` (how it works, features and known issues) and `CHANGELOG.md`. Keep both current, and keep the credit to Germanized in the README, the release notes and the repo description.

## How to work (Karpathy guidelines, adapted)
- **State assumptions, don't hide them.** When something is unclear, pick the most likely reading, say so, and note the call in the commit message or `CHANGELOG.md` (the user asked not to be stopped with questions). Ask only when truly blocked. Say when a simpler approach exists.
- **Simplest change that works.** No features, options or abstractions nobody asked for, and no handling for cases that cannot happen. If a fix could be half the size, rewrite it.
- **Surgical edits.** Touch only what the task needs; match the surrounding style (Germanized's code: `_camelCase` fields, `[Tag]` log prefixes, XML summaries on non-obvious code); don't reformat or refactor working code. Remove only what your own change left unused, and mention other dead code rather than deleting it.
- **Prove it.** Turn each task into a check before coding: a lab step plus a check in `tests/verify-lab.py`, and a mutant in `scripts/mutants.ps1` that makes that check fail when the sync path is cut. Loop until it passes, and report what was not verified (a two-account Steam session can't run in the lab).

## Layout
| Path | What |
| --- | --- |
| `CupheadOnline/` | The BepInEx 5 plugin (`net35`). `Net/` transport (Steam lobbies + P2P, `LanTransport.cs` fallback), `Patches/` Harmony hooks, `Sync/` session state, `UI/` menus and overlays, `Diagnostics/` dev tools and the lab bot (`Lab.cs`). |
| `Assembly-CSharp/` | Decompiled game code (VB). Read it for hook targets; `tools/audit-hooks.ps1` checks every patch target against it. Never package it. |
| `build.ps1` | Hook audit, build, `dist/CupHeads-BepInEx-<version>.zip`. |
| `scripts/setup-lab.ps1`, `run-lab.ps1`, `mutants.ps1`, `tests/verify-lab.py` | Same-PC two-copy lab and its mutation checks. |
| `deps/`, `private/`, `artifacts/`, `dist/` | Local only, git-ignored: BepInEx 5.4.23.5, lab copies, test logs, build output. |

## Rules
- **Patches:** register every new `[HarmonyPatch]` class with `PatchTracked` in `Plugin.cs` (the coverage audit warns otherwise), and run `build.ps1` so the hook audit passes.
- **Authority:** the host owns world, scenes, saves, boss and enemy state; each player owns their own cup's body. A change that lets the non-owner decide something must say why.
- **Wire protocol:** a new or changed packet bumps `NetProtocol.PROTOCOL` in `Net/Packets.cs` and is noted in `CHANGELOG.md`.
- **Lab windows:** copies start minimized (`SW_SHOWMINNOACTIVE`) as normal 640x360 windows at the right edge, lowest quality, 30 fps, BelowNormal priority, one tour at a time; never focus, input or topmost. Lab copies use `lab-saves/`, never the player's saves, achievements or display settings.
- **Releases:** `vX.Y.Z` with `CupHeads-BepInEx-X.Y.Z.zip`, a pre-release until a real two-account test passes; the pinned `bepinex-5.4.23.5` loader release is never deleted. Push, release or edit GitHub only when the user says so; commit as `multiplayereverything <347356261+multiplayereverything@users.noreply.github.com>`. Never commit game files, DLLs, ZIPs, `deps/`, `private/` or tokens.
- **Website:** send version, status, connect, players, description, repo and synced parts to the Website session after a release or a change in test level; it owns `games.json` (site id `cuphead`).
