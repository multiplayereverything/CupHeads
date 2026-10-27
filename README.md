# CupHeads: Cuphead online co-op

Play Cuphead's two-player co-op online: both cups, the same save, bosses, run-and-gun levels, plane levels and the world map, over Steam invites.

> **CupHeads was originally made by [Germanized](https://github.com/Germanized/CupHeads)** (with Sh0kr). This fork is maintained by **Multiplayer Everything** for the [Multiplayer Everything launcher](https://www.multiplayereverything.com). All credit for the mod itself goes to Germanized; our changes are listed in [CHANGELOG.md](CHANGELOG.md).

## Install

**With the Multiplayer Everything launcher:** pick Cuphead and press install. The launcher sets up BepInEx and the mod for you.

**By hand:**

1. Download `BepInEx_win_x64_5.4.23.5.zip` from this repo's [`bepinex-5.4.23.5` release](https://github.com/multiplayereverything/CupHeads/releases/tag/bepinex-5.4.23.5) and unzip it into your Cuphead folder (the one with `Cuphead.exe`, usually `C:\Program Files (x86)\Steam\steamapps\common\Cuphead`).
2. Download `CupHeads-BepInEx-<version>.zip` from the [latest release](https://github.com/multiplayereverything/CupHeads/releases) and unzip it into the same folder. The mod ends up at `BepInEx\plugins\CupheadOnline\CupheadOnline.dll`.
3. Start Cuphead from Steam. `MULTIPLAYER` appears on the main menu.

Both players need the same mod version (the handshake refuses a mismatch and tells both players which versions they have). There is no installer any more: the mod is a plain BepInEx plugin.

## Playing together

**Online (Steam):** the host opens `MULTIPLAYER`, picks `HOST A GAME`, then `INVITE A FRIEND`. The friend accepts the Steam invite from the overlay, Steam chat or the friends list (even with Cuphead closed). Once both are in, the host picks `SAVE FILE` and `YOU PLAY` and presses `START!`. After you've played once, `PLAY WITH <name>` does all of it in one press.

**LAN / direct IP (fallback):** the host picks `HOST ON LAN` and then `COPY LOBBY CODE`, which copies the host's address (for example `192.168.1.20:27140`). The friend copies that address and picks `JOIN A GAME`. This needs TCP port 27140 reachable on the host, so it is meant for the same network or a VPN; online play should use Steam.

The host plays Player One and owns the save; the guest plays Player Two on a live copy of the host's save (the guest's own save files are not written during a session, and are backed up first).

## How it works

| Part | What it is |
|---|---|
| **Loader** | BepInEx 5.4.23.5 (x64, Unity 2017.4 Mono). The plugin `com.cupheadonline.mod` loads into `Cuphead.exe` and applies 88 Harmony patch classes one by one, so one broken hook doesn't stop the rest; a failed hook shows a warning on the menu. `tools/audit-hooks.ps1` checks all 82 patch targets and reflected member names against the decompiled game in `Assembly-CSharp/` before every build. |
| **Transport** | Steam, through the game's own Steamworks.NET (`Assembly-CSharp-firstpass`, `steam_api64.dll`): friends-only Steam lobbies (`SteamMatchmaking`) for invites, joining and the participant roster, and Steam P2P packets (`ISteamNetworking.SendP2PPacket`, reliable and unreliable channels, `AllowP2PPacketRelay(true)`), which Steam carries over Valve's relay when a direct route isn't possible. No port forwarding and no server of ours. The LAN fallback (`Net/LanTransport.cs`) carries the same packets over TCP with synthetic peer IDs. |
| **Handshake** | Hello / Welcome / Ready carrying the mod version and wire protocol 4; mismatches are refused with a message. A ping every second measures latency; a silent peer times out and a guest auto-reconnects to the same lobby. |
| **Authority** | Host-authoritative world: scene changes, save selection, level start, boss and enemy state, RNG seeds, Dice Palace rolls, level outcome, pause, retries and menus. Each player owns their own cup's body (damage and health), so getting hit never waits on the network. |
| **Players** | The guest streams input frames (stick, held buttons and per-button press counters, so jumps, dashes and parries survive packet loss). The host simulates the guest's cup from them and sends player states back; the guest predicts its own cup and reconciles against the input the host acknowledged. The host's cup is a proxy on the guest, smoothed by a ping-adaptive jitter buffer. |
| **Damage, death, revive** | A hit on your own cup is replayed on the other machine as a DamageEvent. Each owner also streams its cup's health and life state; if the other machine's copy disagrees for more than 1.5 s (a replayed hit landed during that copy's invulnerability), it follows the owner, down to dying or back up after a revive. Revives go through the host: a ghost leaving the screen asks the host for a partner's heart, and a parry on either machine is forwarded so the ghost's owner revives too. |
| **Bosses and enemies** | Enemies get deterministic cross-machine IDs; the host sends their positions, animation phase and boss HP. The guest applies boss HP through the game's own damage path (it never undoes a phase). A desync sentinel compares the host's boss HP and positions with the guest's every few seconds and resyncs after repeated mismatches. |
| **Levels and the map** | The host's scene loads (levels, map, shops, cutscenes, results) are sent to the guest with an RNG seed; scene requests the guest makes (entering a level, Retry, Exit to Map) are forwarded to the host. A level-ready gate holds whichever player loaded first, so the fight starts on the same frame. The guest's map cup is Player Two on the host's map. |
| **Save** | The guest plays on an in-memory mirror of the host's save (coins, items, unlocks, NPC progress), sent in chunks; the mirror is never written to the guest's disk. |

## Features and known issues

| Area | Status | Notes |
|---|---|---|
| Steam invites, lobbies, PLAY WITH | ✅ works | Two different Steam accounts are needed; one account can't P2P to itself. |
| LAN / direct IP | ✅ works | Fallback only; TCP 27140 on the host. |
| Both cups move (map and levels) | ✅ lab-tested | |
| Level and world-map transitions | ✅ lab-tested | Host leads; guest requests are forwarded. |
| Boss HP and phases | ✅ lab-tested | |
| Hits, death, revive (ghost and parry) | ✅ lab-tested | |
| Shared save, shops, equip cards, pause, Dice Palace | ✅ upstream feature | Not covered by the lab tour yet. |
| Ms. Chalice (DLC) | ✅ upstream feature | Needs the DLC on both PCs. |
| 3 or more players | ⚠️ experimental | Extra players are tracked and drawn, but many scenes assume two players. |
| Fights against a real second player over the internet | ⚠️ needs a playtest | The lab runs two copies on one PC over LAN; Steam P2P between two accounts still needs a real two-account test. |

## In-game extras

* Shared pause, a connection tag (partner, ping, short messages), a session card beside the pause menu, a team results card, optional boss health bars and a Battle Assist HUD.
* Hotkeys: `F1` shortcuts card, `F6` quick resync, `F7` boss health bars, `F8` session panel, `F9` copy diagnostics, `F10` Battle Assist HUD, `F11` Dev Lab, hold `C` then `1`-`4` for the comm wheel.
* Save backups to `BepInEx/CupHeads/SaveBackups/` when an online session starts, an update notice when a newer release is out, and switching between keyboard and controller at any time.
* Settings live in `BepInEx/config/com.cupheadonline.mod.cfg`. `[Networking] LatencyFriendlyDamage = false` switches to strict host-owned damage.

## Building from source

Needs the .NET SDK (8 or later) and Cuphead installed through Steam.

1. Unzip `BepInEx_win_x64_5.4.23.5.zip` into `deps/BepInEx-5.4.23.5/` (the build references BepInEx from there, so the game folder doesn't need it), or install BepInEx into the game.
2. Run `.\build.ps1 -Release`. It finds Cuphead through Steam's library folders (or `-CupheadPath` / `$env:CUPHEAD_PATH`), audits the Harmony hooks, builds `CupheadOnline.dll`, and writes `dist/CupHeads-BepInEx-<version>.zip`. Add `-NoDeploy` to skip copying the DLL into the game's `BepInEx/plugins/CupheadOnline/`.

`Assembly-CSharp/` is the decompiled game code the hook audit reads; it is kept for development only and is never packaged.

## Testing

The lab runs two isolated game copies on one PC, connects them over the LAN fallback (one Steam account can't P2P to itself) and has a scripted bot play both sides. The copies start minimized as small 640x360 windows at the lowest quality and BelowNormal priority; no keyboard, mouse or focus is used, and the player's saves, achievements and display settings are left alone (lab saves live in each copy's `lab-saves/`).

```powershell
.\scripts\setup-lab.ps1          # once: private/lab-host and private/lab-client, linked to the Steam install
.\scripts\run-lab.ps1            # one tour, judged by tests/verify-lab.py
.\scripts\mutants.ps1            # each mutation drops one kind of packet; its checks must fail
```

The tour: connect, host starts a save, both reach the world map, each cup moves on the map, the host loads The Root Pack, each cup moves in the level, the host damages the boss, the guest takes hits until it dies, is revived through the host, dies again and is revived by the host parrying its ghost, then both return to the map. `verify-lab.py` checks every step from both copies' logs (`artifacts/tests/<run>/`).

The F11 Dev Lab (one game, Player Two driven through the remote-input path) and the UI flag files below are still there for quick checks:

* `cupheads-uitour.flag`: the game drives itself through every CupHeads screen, screenshots each to `BepInEx/CupHeadsUiTour/`, and quits.
* `cupheads-presstest.flag`: asks for one press on a spare controller and logs whether it was handed over.
* `cupheads-uipreview.flag`: in the lobby, `Insert` steps through every lobby phase with sample data.
* `cupheads-uidump.flag`: dumps the save-select UI hierarchy and sprites to `BepInEx/CupHeadsUiDump/`.

## Credits

* **Germanized** (and Sh0kr) made CupHeads: [Germanized/CupHeads](https://github.com/Germanized/CupHeads). Made for Daniel; special thanks to Internallinked.
* This fork is maintained by **Multiplayer Everything** for the Multiplayer Everything launcher.
* BepInEx (LGPL-2.1) for the mod loader, Harmony for patching.

## Disclaimer

CupHeads is an independent fan project and is not affiliated with Studio MDHR, Cuphead, Microsoft, Valve, or Steam. You need your own copy of Cuphead.
