# Granny Co-op

**English** · [Русский](README.ru.md)

A co-op prototype for **Granny** (DVloper, 2018), the Steam PC build. Two players share
one house: they see each other move, Granny is host-authoritative, and doors, the shared
item flags and the catch state cross the wire. When Granny catches one player, that
player's client keeps watching the other one for the whole death sequence.

This is a MelonLoader mod against the game's **IL2CPP** Unity 6 build. It is a prototype:
the networking and the door/item sync are tested with a scripted two-instance harness, but
nobody has played the game through in co-op yet.

| | |
|---|---|
| Game | Granny (2018), DVloper — Steam app **962400** |
| Build tested | buildid **21006637** (v1.3.4), Unity **6000.0.58f2**, IL2CPP, metadata v31 |
| Loader | MelonLoader **0.7.3** |
| Mod version | **0.5.3** |
| Players | 2 (host + one guest). The protocol has no hard limit, 3+ is untested |
| Transport | UDP, direct IP or virtual LAN, port **47777** |
| QA | `automated` — scripted two-instance runs against real game clients |

## Where things stand

Two players can host and join, see each other's avatars, see the same Granny (she is
simulated on the host and pinned on the guest), and open/close the same doors
(bidirectionally, host-authoritative). The shared inventory flags sync both ways, but the
game reverts the "have" flag on its own a few seconds after a write, so items are not
dependable yet. The van and the day cycle are **not** synced, and a catch is not replayed
on the other client: each side plays its own death, and the wire carries only the
dead/alive edge, which switches the caught player to a spectator view of the other one.

A guest follows the host between scenes (SplashScreen → Menu → gameplay), including a
host that announces its scene while the guest is still booting.

## What works

Verified in scripted runs on 2026-10-23 (details and log excerpts in
[`docs/NOTES.md`](docs/NOTES.md)):

- **Players** — host and guest spawn avatars for each other, 20 Hz transform stream,
  name above the head (`§11`, `§12`).
- **Granny** — host publishes transform and AI flags at 20 Hz; the guest turns her
  `NavMeshAgent` off and pins her transform every frame with a 2.5 m snap threshold.
  Joint tracking to 0.1 m (`§12`).
- **Doors** — all 48 doors in the house are mapped by name hash at prepare. Every change
  is sent as a `Door/State`; the host also sends a full snapshot every 10 s; a guest that
  changes a door sends a `Door/Request` that the host validates (locked doors are forced
  back to locked). Last result: **0 door mismatches in 13 paired samples** with the host
  as the only flipper (`§23`).
- **Items** — the shared item table is snapshotted at prepare, changes are synced as
  `Item/State` and repaired by a 10 s snapshot. The wire path works; the game's own
  behaviour does not (see Known issues).
- **Scene follow** — the host is authoritative for the scene; a guest loads whatever the
  host announces, with a guard that holds the request while the guest is still in
  `SplashScreen`, so the game's boot is not skipped (`§24`).
- **Catch and the death view** — a catch, or the game's own end sequence, is latched as
  dead and sent as a `Catch` edge. While dead the client's camera follows the other player
  from eye height, and the game's full-screen fade and blood overlay are hidden so the view
  is visible. The latch is held through the whole sequence (Granny carrying the body can
  take a minute) and released when the game puts the player back in play — about 2 s after
  the `Player` root reactivates — or 45 s after the game's flags go clear, or at once on a
  scene change. Verified under the scripted host: 46.6 s of continuous tracking through the
  game's camera swaps, and in a later run a 28.3 s view that ended 2.0 s after the revive;
  a forced scene change while dead tore the view down before the load and told the peer the
  player is alive again (`§26`). On the other side, a `Catch` edge hides the dead peer's
  avatar until the peer announces it is alive again.
- **Late join** — a guest that loads a scene after the host gets state from one snapshot,
  not from an event log (`§16`).
- **Failure paths** — kill the client and rejoin; kill the host; restart the host under a
  live guest (the same client process rejoins with no user action) (`§17`–`§19`).
- **Impaired link** — passes over the chaos relay with the `bad_wifi` profile
  (35 ± 25 ms, 2 % loss, bursts, 2 % reorder, 0.5 % duplicate), and recovers from a full
  8 s two-way outage (`§20`, `§22`).

## Known issues

- **The item "have" flag self-reverts.** About 7–8 s after a write, the game sets it back
  on its own, on both ends, including on an end that never calls `CheckInventory`. Items
  therefore drift back to whatever the game re-derives. Item visuals (the model in the
  hand) are not moved by the flag at all. This is the main open gameplay bug (`§15`,
  `§23`).
- **The catch is only partly synced.** Each client is caught by its own session, and the
  mod does not replay a catch on the peer: no jail, no shared death screen. The wire
  carries the dead/alive edge; the peer shows it in its status line (`rcatch=`) and hides
  the dead player's avatar until it comes back. Re-entry after a catch is untested (`§23`,
  `§26`).
- **The game's own death → game-over → Menu path is untested.** The view's exit on a scene
  change was exercised by forcing one while dead (`§26`); a death that runs on into the
  game's own game-over screen has not been observed, so whether the view ends exactly when
  the game goes back to the Menu by itself is unknown (`§26`).
- **Scene follow skips the difficulty selection.** A guest that follows the host into
  `Scene` loads the scene directly, not through Play → difficulty → Continue. The world
  builds and the player becomes active anyway, but whether the choice matters later is
  unknown, and the path has not been exercised in a real pair (`§24`).
- **Item deltas are incomplete.** Only the have flag is carried; counts/deltas are not.
- **No GUI.** Configuration is MelonPreferences plus command-line flags.
- **Door visuals after an apply are unverified.** The state is read from the game's own
  component; whether the animation, collider and sound match the state is not observable
  in a log and has not been checked on screen.
- **One machine only so far.** Every run was two game processes plus a relay on one PC.
  No run has crossed a real network.

## How the game works (for this mod)

- Unity **6000.0.58f2** IL2CPP, D3D12, no Mono assemblies. All game logic is managed C#
  compiled to IL2CPP; the metadata keeps class, field and method names, which is why a
  Harmony/MelonLoader mod is possible at all.
- **No netcode** in the game, but Steamworks.NET is compiled in. This mod does not use
  Steam at all; Steam is a later option for NAT traversal.
- The player controller is `FirstPersonController_Egen`; the avatar body is found with
  `GetComponentInChildren<CharacterController>()`.
- Doors are `Door` components under `All doors/…`; the mod keys them by a hash of the
  transform path, because the enumeration order differs between installations.
- Inventory is a fixed set of flags (`InventoryController`), one per item type.

## Protocol v1

UDP, little-endian, one datagram per message, no fragmentation at this layer.

```
magic "GCP1" (4) | type (1) | seq (4) | payload
```

| type | name | direction | payload |
|---|---|---|---|
| 1 | Hello | guest → host, repeated until answered | protocol version, name |
| 2 | Welcome | host → guest | protocol version, host id, host name |
| 3 | Reject | host → guest | reason (`protocol v1 required`, `session full`) |
| 4 | State | both, 20 Hz | sender id, flags, position, yaw, pitch |
| 5 | Scene | host → guest | scene name |
| 6 | Bye | both, on leave | — |
| 7 | Granny | host → guest, 20 Hz | flags, position, yaw |
| 8 | Door | both | kind (State / Snapshot / Request / SnapshotRequest), door hash, state |
| 9 | Item | both | kind (State / Snapshot / SnapshotRequest), item hash, have flag |
| 10 | Catch | both, on edge | alive (0 dead / 1 back), cause (informational) |

Host is authoritative for the scene, Granny, doors and items. The receiver has **no reordering
logic** — `seq` is written but not used to order. Sockets on both sides time out after
5 s of silence and fall back to single-player.

## Build

Requirements: the .NET 6 SDK, MelonLoader 0.7.3 installed into a **copy** of the game
(the loader's generated `Il2CppAssemblies` are the references), and Windows.

```
set GRANNY_TEST=<game dir with MelonLoader installed>
dotnet build -c Release
```

The project reads `$(GRANNY_TEST)` and links MelonLoader plus the Il2Cpp assemblies from
it; it never writes into that folder. The output is `bin/Release/GrannyCoop.dll` (net6.0,
no extra dependencies). Copy it into `<game dir>/Mods/`.

Do not run this against your only install: copy the game folder first, as the harness
does (`C:\Users\Public\granny-test` and `...\granny-test2`).

## Run

Host:

```
Granny.exe --coop-host --coop-name=Host
```

Guest:

```
Granny.exe --coop-join=192.168.1.10 --coop-name=Guest
```

`--coop-port=N` overrides the port (default 47777). Without command-line flags, set
`Mode` (`Off`/`Host`/`Join`), `Address`, `Port` and `PlayerName` in
`UserData/MelonPreferences.cfg`. A host needs UDP 47777 reachable; on two machines on the
same LAN or a virtual LAN (ZeroTier/Tailscale/Radmin) that is the whole setup.

Test-only flags used by the harness: `--coop-autoplay` (drive the menu), `--coop-testmove`
(walk a fixed path), `--coop-doortest`, `--coop-itemtest`, `--coop-probe`,
`--coop-spectate` (force the spectator view without dying) and `--coop-catchtest` (walk
onto Granny so she catches the player). Do not use them for real play.

## QA

The harness in [`tools/`](tools/) starts both copies, optionally with a chaos relay
between them, and collects both MelonLoader logs into one run file.

```
powershell -ExecutionPolicy Bypass -File tools/run-pair.ps1 -Seconds 300 -DoorTest
powershell -ExecutionPolicy Bypass -File tools/run-pair.ps1 -Seconds 240 -DoorTest -Relay `
    -RelayProfile bad_wifi -RelaySeed 1042 -ChaosRoot <path to a coop-anything checkout>
python -I tools/analyze-soak.py <run file>     # paired status samples, mismatches, memory
python -I tools/door-order.py <packets.jsonl>  # seq inversions in the chaos log
python -I tools/fake-host.py --port 47778 --scene Menu --scene-at 10 --scene-until 20 \
    --scene Scene --scene-at 25 --scene-until 40 --seconds 60
```

`run-pair.ps1` refuses to start if a `Granny.exe` is already running, tracks every
process it spawned (and only those), and stops them at the end. `-Relay` needs the chaos
relay from [`coop-anything`](https://github.com/cranebeaker/coop-anything) via
`-ChaosRoot`; without it the pair talks directly.

`tools/fake-host.py` is a scripted protocol-v1 host for join-side tests with **one** game
instance: it answers `Hello` with `Welcome`, sends `Scene` on a schedule and keeps the peer
timeout alive. It is how scene follow was checked without a second client (~2.7 GB instead
of ~5.4 GB). `--state-circle RADIUS` adds a fake remote player walking a circle, which is
what the spectator camera can be checked against:

```
python -I tools/fake-host.py --port 47778 --state-circle 3 --scene Scene \
    --scene-at 15 --scene-until 300 --seconds 300
```

`--catch-at S` and `--revive-at S` send the peer's dead/alive `Catch` edges at those
times, which is how the survivor-side avatar hiding is checked without a second client.

A single-instance run has to start the game with the test folder as its working directory
(`Start-Process ... -WorkingDirectory <game dir>`). Launched from anywhere else, Steamworks
`RestartAppIfNecessary` hands the launch back to Steam, the test process exits and the
untouched install starts instead (`docs/NOTES.md §25`).

| Run | Date | What it covered | Result |
|---|---|---|---|
| two-instance, 110–160 s ×4 | 2026-10-23 | player + Granny | joint tracking to 0.1 m, no errors |
| `-DoorTest`, late join | 2026-10-23 | 48 doors, one flipper | converged from snapshots |
| `-ClientDoorTest` | 2026-10-23 | guest → host requests | request path works, no ping-pong |
| kill client, rejoin | 2026-10-23 | 5 s timeout, fresh process | rejoin repaired by snapshot |
| kill host / restart host | 2026-10-23 | 5 s timeout, same process rejoins | recovered, ~6 s to door agreement |
| `bad_wifi`, 240 s | 2026-10-23 | 131 c2s + 269 s2c drops, 40 dups | converged |
| 8 s full outage | 2026-10-23 | both directions down | recovered in 10.07 s (~2 × 5 s timeout) |
| `-DoorTest` 220 s of 420 s | 2026-10-23 | v0.4.7, host-only flipper | **0/13 door mismatches**; run cut short by machine memory pressure |
| `fake-host.py`, one client | 2026-10-23 | scene follow both ways, boot guard | Menu → Scene and Scene → Menu followed; SplashScreen request held, load fired on the boot transition |
| `fake-host.py` + `--coop-catchtest` | 2026-10-23 | catch and the death view (v0.5.0) | catch → 46.6 s of continuous spectating through `Camera`/`PlayerEyes`/`PlayerCamInBed`; death overlay hidden |
| `fake-host.py` + `--coop-catchtest` | 2026-10-23 | revive releases the view (v0.5.1) | 28.3 s spectating the grab and carry, stopped 2.0 s after `player active -> True` |
| `fake-host.py --catch-at/--revive-at` | 2026-10-23 | remote catch on the survivor (v0.5.2) | `Catch` edge hid the peer's avatar (`avVisible=False`, `rcatch=granny`), shown again on the revive |
| `fake-host.py` + `--coop-catchtest` | 2026-10-23 | scene change while dead (v0.5.3) | forced `Menu` request during the death → spectate stopped before the load, host received `Catch alive` |

No run has covered: a real network, three players, a full playthrough, or re-entry after
a catch.

## Notes and prior art

The full engineering log — every run, counter, dead end and open question — is in
[`docs/NOTES.md`](docs/NOTES.md). It is long on purpose; the short version is the table
above.

Existing Granny projects all target a different build (their type names do not exist in
this one) and none is a co-op mod for this game. *Granny: Escape Together* (Steam
3070520) is a different game by a different developer. See `docs/NOTES.md §3`–`§4`.

## License

MIT — see [`LICENSE`](LICENSE). The chaos relay used by the QA harness is MIT too, from
[`coop-anything`](https://github.com/cranebeaker/coop-anything).
