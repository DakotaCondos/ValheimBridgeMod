# StreamBridge — Spawnable Guide

This guide lists common **spawnable mob prefabs** and shows how to trigger them via StreamBridge's **JSON /command API**.
Prefab names are **case‑sensitive** and must match your Valheim version/mod set.

> Spawning is **host-only** and **blocked in interiors/dungeons** (caves/mines/crypts). Grounded patterns snap to terrain and run safety checks (slope, headroom, clear radius).

---

## Spawning API (JSON)

**POST** `http://127.0.0.1:<Port>/command`  (default Port: **28575**)

```jsonc
{
  "commands": [
    {
      "id": "c1",                // optional
      "type": "spawn.front" | "spawn.ring" | "spawn.line" |
              "spawn.arc"   | "spawn.wedge"| "spawn.grid" |
              "spawn.scatter" | "spawn.rain",
      "payload": {
        "prefab": "Skeleton",   // required (mob prefab)
        "stars": 1,             // 0..2 (levels 1..3)
        // Pattern fields (examples below):
        // front:   count, distance
        // ring:    count, radius
        // line:    count, spacing, distance
        // arc:     count, radius, spanDeg
        // wedge:   count, spacing, distance
        // grid:    rows, cols, spacing, distance
        // scatter: count, radius, distance
        // rain:    count, radius, height
        // Common optionals:
        "delay": 0.25,          // seconds between spawns (clamped by MaxDelay)
        "ttl": 20               // seconds to auto‑despawn (0 = none; clamped by MaxTTL)
      }
    }
  ]
}
```

**Config clamps (from StreamBridge.cfg):** `MaxSpawned`, `MaxDelay`, `MaxTTL`, `MaxStars`.

---

## Quick Examples

### curl
Ring of 1★ Skeletons (6) around you, radius 10m, 0.25s stagger, TTL 20s:
```bash
curl -X POST "http://127.0.0.1:28575/command"   -H "Content-Type: application/json"   -d '{ "commands": [ { "id":"c1","type":"spawn.ring","payload":{"prefab":"Skeleton","stars":1,"count":6,"radius":10,"delay":0.25,"ttl":20} } ] }'
```

Rain down 12 Skeletons from 15m up within a 10m radius:
```bash
curl -X POST "http://127.0.0.1:28575/command"   -H "Content-Type: application/json"   -d '{ "commands": [ { "id":"c1","type":"spawn.rain","payload":{"prefab":"Skeleton","stars":1,"count":12,"radius":10,"height":15,"delay":0.25,"ttl":20} } ] }'
```

### PowerShell
Front formation of Greydwarfs (5) at 8m, 0.2s stagger, TTL 20s:
```powershell
$body = @{
  commands = @(
    @{ id="c1"; type="spawn.front"; payload=@{ prefab="Greydwarf"; stars=0; count=5; distance=8; delay=0.2; ttl=20 } }
  )
} | ConvertTo-Json -Depth 6
iwr "http://127.0.0.1:28575/command" -Method POST -ContentType "application/json" -Body $body
```

Grid of Greydwarfs (3×4), spacing 3m, 12m away:
```powershell
$body = @{
  commands = @(
    @{ id="c1"; type="spawn.grid"; payload=@{ prefab="Greydwarf"; stars=0; rows=3; cols=4; spacing=3; distance=12; delay=0.1; ttl=20 } }
  )
} | ConvertTo-Json -Depth 6
iwr "http://127.0.0.1:28575/command" -Method POST -ContentType "application/json" -Body $body
```

---

## Spawnable Characters by Biome

*(Bosses marked with **(Boss)**; tameables marked with **(Tameable)**.)*

### Meadows
- Boar **(Tameable)**
- Neck
- Deer
- Greyling
- Eikthyr **(Boss)**

### Black Forest
- Greydwarf
- Greydwarf_Shaman
- Greydwarf_Elite
- Troll
- Skeleton
- gdking *(The Elder)* **(Boss)**

### Swamp
- Draugr
- Draugr_Elite
- Blob
- Wraith
- Leech
- Surtling
- Abomination *(Mini-boss)*
- Bonemass **(Boss)**

### Mountains
- Wolf **(Tameable)**
- Drake
- StoneGolem
- Fenring
- Ulv
- Dragon *(Moder)* **(Boss)**

### Plains
- Lox **(Tameable)**
- Deathsquito
- Fuling
- FulingBerserker
- FulingShaman
- FulingArcher
- GoblinKing *(Yagluth)* **(Boss)**

### Ocean
- Serpent

### Mistlands
- Seeker
- SeekerBrood
- SeekerSoldier
- Gjall
- Tick
- SeekerQueen **(Boss)**

---

## Unsafe / Excluded Prefabs

- **Player** (and variants) — excluded for safety.
- Any dev/test placeholder prefabs that may crash clients or servers.

---

## Tips

- Names must be exact. If a spawn fails with “prefab not found”, check capitalization and your mod set.
- Grounded patterns reject steep slopes or blocked headroom; try flatter ground or smaller counts/radii.
- Interiors/dungeons are blocked for grounded patterns by environment name (caves, crypts, mines, etc.). Use **spawn.rain** outdoors if you need vertical drops.

---

© 2025 StreamBridge contributors
