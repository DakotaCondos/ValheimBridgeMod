# StreamBridge (Valheim) — User Guide

**Version:** 1.0.1  
**Game:** Valheim (PC) + BepInEx 5  
**Transport:** HTTP on `http://127.0.0.1:<Port>/command` (localhost only)

> StreamBridge is a **host-only tool** for spawning mobs, giving items, and applying effects via simple JSON over HTTP.  
> It is intended to be driven by your own apps/bots, not exposed to the internet.

---

## Installation

1. Install **BepInEx 5** for Valheim.
2. Build this mod in **Release** and copy the DLL to  
   `Valheim\BepInEx\plugins\StreamBridge\StreamBridge.dll`
3. Launch Valheim. On first run, StreamBridge will create its config at:  
   `Valheim\BepInEx\StreamBridge\StreamBridge.cfg`

---

## Configuration (created automatically)

File: `BepInEx\StreamBridge\StreamBridge.cfg`

| Section | Key         | Default | Notes |
|--------:|-------------|:------:|------|
| Network | **Port**    | 28575  | HTTP listens on localhost only (`127.0.0.1` and `localhost`). |
| Limits  | **MaxSpawned** | 50  | Max spawn count **per command**. |
| Limits  | **MaxDelay**   | 30  | Max stagger delay (seconds) per command. |
| Limits  | **MaxTTL**     | 600 | Max time-to-live (seconds) for spawned entities. |
| Limits  | **MaxStars**   | 2   | Max allowed stars (0..2). |
| UI      | **Notify**     | true| When true, in-game HUD toasts are shown for actions. |

> Change values with Valheim closed, or edit and then reload the mod.

---

## Endpoint

`POST http://127.0.0.1:<Port>/command`

- **CORS:** Allowed for local tools (wildcard `Access-Control-Allow-Origin: *`). Preflight `OPTIONS` returns `204`.
- **Only POST is accepted** (besides `OPTIONS`). Other methods return errors.
- **Body:** JSON object with a `commands` array. Each command is independent and affects only **one** thing.

### Request Schema

```jsonc
{
  "commands": [
    {
      "id": "c1",            // optional string (echoed back)
      "type": "item.give" | "effect.apply" |
              "spawn.front" | "spawn.ring" | "spawn.line" |
              "spawn.arc"   | "spawn.wedge"| "spawn.grid" |
              "spawn.scatter" | "spawn.rain",
      "payload": { /* type-specific fields below */ }
    }
  ]
}
```

### Response Schema

```jsonc
{
  "ok": true,
  "results": [
    {
      "id": "c1",
      "type": "item.give",
      "ok": true,
      "queued": true,
      "meta": { /* sanitized inputs, e.g. clamped values */ }
    },
    {
      "id": "c2",
      "type": "spawn.ring",
      "ok": false,
      "error": "validation_failed",
      "message": "payload.prefab required"
    }
  ]
}
```

If you are not the host/server, each result returns `{ ok:false, error:"not_host" }`.

---

## Commands & Payloads

> **Names are case-sensitive.** Use exact prefab/effect names from Valheim.  
> Spawns require **host/server** (single-player counts). Items and effects also run on host by design.

### 1) Give Items — `type: "item.give"`

```jsonc
"payload": {
  "prefab": "Wood",   // string, required
  "amount": 25,       // int >= 1 (default 1)
  "quality": 1,       // int >= 1 (clamped to item maxQuality; default 1)
  "variant": 0        // int >= 0 (default 0)
}
```

Behavior:
- Splits into legal stack sizes; adds to inventory or drops gently at feet if full.
- When `Notify=true`, shows standard pickup messages.

---

### 2) Apply Effects — `type: "effect.apply"`

```jsonc
"payload": {
  "name": "Rested",   // string, required (exact status effect name)
  "duration": 20      // seconds, float; 0 = use effect default; otherwise clamped [1..600]
}
```

Behavior:
- Refreshes an existing same-name effect (remove+add) so time and modifiers reset.
- HUD toast is shown when `Notify=true`.

---

### 3) Spawn — `type: "spawn.*"`

Common fields (all patterns):
```jsonc
"payload": {
  "prefab": "Skeleton", // required, exact mob prefab name
  "stars": 1,           // 0..2 (maps to levels 1..3)
  // pattern-specific fields below,
  // plus optional:
  "delay": 0.25,        // seconds between spawns (0..MaxDelay), default 0
  "ttl": 20             // seconds before auto-despawn (0..MaxTTL), default 0 (=no auto)
}
```

Grounded patterns (use terrain projection + safety checks):
- **front**, **ring**, **line**, **arc**, **wedge**, **grid**, **scatter**
  - Snap to ground, check slope, clear headroom, avoid player overlap.
  - **Blocked in interiors/dungeons** (caves/mines/crypts) — by environment name.

Non-grounded pattern:
- **rain** — spawns at height above/around the player and lets entities fall naturally.

Pattern payloads:

- **spawn.front**
  ```jsonc
  { "count": 5, "distance": 8, "delay": 0.2, "ttl": 20 }
  ```

- **spawn.ring**
  ```jsonc
  { "count": 10, "radius": 10, "delay": 0.25, "ttl": 30 }
  ```

- **spawn.line**
  ```jsonc
  { "count": 8, "spacing": 3, "distance": 15, "delay": 0.15, "ttl": 15 }
  ```

- **spawn.arc**
  ```jsonc
  { "count": 6, "radius": 8, "spanDeg": 120, "delay": 0.15, "ttl": 20 }
  ```

- **spawn.wedge**
  ```jsonc
  { "count": 9, "spacing": 3, "distance": 10, "delay": 0.1, "ttl": 20 }
  ```

- **spawn.grid**
  ```jsonc
  { "rows": 3, "cols": 4, "spacing": 3, "distance": 12, "delay": 0.1, "ttl": 20 }
  ```

- **spawn.scatter**
  ```jsonc
  { "count": 20, "radius": 12, "distance": 10, "delay": 0.05, "ttl": 8 }
  ```

- **spawn.rain**
  ```jsonc
  { "count": 12, "radius": 10, "height": 15, "delay": 0.25, "ttl": 20 }
  ```

Clamps enforced by config:
- `stars` → 0..**MaxStars**
- `delay` → 0..**MaxDelay**
- `ttl`   → 0..**MaxTTL**
- spawn counts/rows/cols are capped by **MaxSpawned**

---

## Examples

### Minimal Batch
```json
{
  "commands": [
    { "id": "c1", "type": "item.give", "payload": { "prefab": "Wood", "amount": 25 } },
    { "id": "c2", "type": "effect.apply", "payload": { "name": "Wet", "duration": 20 } }
  ]
}
```

### Mixed Spawns + Items
```json
{
  "commands": [
    { "id": "c1", "type": "spawn.ring", "payload": { "prefab": "Skeleton", "stars": 1, "count": 6, "radius": 10, "delay": 0.25, "ttl": 20 } },
    { "id": "c2", "type": "item.give",  "payload": { "prefab": "FineWood", "amount": 10, "quality": 1, "variant": 0 } },
    { "id": "c3", "type": "spawn.line", "payload": { "prefab": "Draugr", "stars": 2, "count": 8, "spacing": 3, "distance": 15, "delay": 0.15, "ttl": 15 } }
  ]
}
```

### PowerShell
```powershell
$body = @{
  commands = @(
    @{ id="c1"; type="spawn.grid"; payload=@{ prefab="Greydwarf"; stars=0; rows=3; cols=4; spacing=3; distance=12; delay=0.1; ttl=20 } },
    @{ id="c2"; type="item.give";   payload=@{ prefab="Resin"; amount=20; quality=1; variant=0 } }
  )
} | ConvertTo-Json -Depth 6

iwr "http://127.0.0.1:28575/command" -Method POST -ContentType "application/json" -Body $body
```

### curl
```bash
curl -X POST "http://127.0.0.1:28575/command"   -H "Content-Type: application/json"   -d '{
        "commands":[
          {"id":"c1","type":"spawn.rain","payload":{"prefab":"Skeleton","stars":1,"count":12,"radius":10,"height":15,"delay":0.25,"ttl":20}},
          {"id":"c2","type":"effect.apply","payload":{"name":"Rested","duration":120}}
        ]
      }'
```

---

## Safety & Behavior Notes

- **Grounding & clearance:** Grounded patterns snap to the terrain, reject steep slopes, ensure headroom/clear radius, and avoid placing directly on the player.
- **Interiors blocked:** Grounded spawns are disabled in dungeon/cave/mine/crypt environments (identified by current environment name).
- **Stars to level:** `0 → lvl1`, `1 → lvl2 (★)`, `2 → lvl3 (★★)`.
- **TTL:** Spawns can auto-despawn after N seconds.
- **Logging:** Minimal at **Info** level. For details, inspect the HTTP responses or BepInEx log.

---

## Troubleshooting

- **400 `bad_request`** – JSON invalid, missing `commands`, or missing required payload fields.
- **404 `not_found`** – Path is not `/command`.
- **405** – Method not allowed (use `POST`).
- **`not_host`** – You must be the host/server for spawns/effects/items via StreamBridge.
- **`Prefab/Item/Effect not found`** – Name mismatch; use exact case-sensitive prefab/effect names used by your Valheim version/mod set.

---

## Dev Tester (HTML)

A local helper page (`CommandsTester.html`) is included for building/sending JSON.  
Open it directly in your browser and point it at your endpoint (default `http://127.0.0.1:28575/command`).

---

## Security

- StreamBridge binds to `127.0.0.1` only. If you proxy/forward it, **you** are responsible for access control and rate limiting.

---

© 2025 StreamBridge contributors
