# StreamBridge (Valheim)

A lightweight **host-only** bridge for Valheim that listens on `localhost` and executes gameplay actions sent as **JSON** over HTTP. It is designed to be used by companion apps/bots during streams (e.g., chat-triggered spawns, item rewards, or temporary effects).


---

## Features

- **Local HTTP endpoint**: `POST /command` on `127.0.0.1:<Port>` (default **28575**).
- **Batch JSON**: Send multiple commands in one request; each command targets a single thing.
- **Services**: `SpawnService`, `ItemService`, `EffectService` encapsulate game logic.
- **Spawn patterns**: `front`, `ring`, `line`, `arc`, `wedge`, `grid`, `scatter`, `rain`.
- **Safety**: Terrain projection & clearance checks; **blocked in interiors/dungeons**; optional **TTL** for auto-despawn.
- **Items**: Add to inventory or drop at feet when full; quality/variant supported.
- **Effects**: Apply status effects by name; optional duration.
- **CORS-ready**: Permissive headers and `OPTIONS` preflight for local web tools.
- **Minimal logging**: Info-level; detailed info returned in response bodies.
- **Config auto-creation**: On first run at `BepInEx/StreamBridge/StreamBridge.cfg`.
- **HTML tester**: `CommandsTester.html` for building/sending JSON quickly.

---

## Requirements

- **Valheim (PC)**  
- **BepInEx 5** (e.g., 5.4.x)  
- Windows recommended (other OSes via Wine/Proton may work but are untested here).

---

## Installation

1. Install **BepInEx 5** for Valheim.
2. Build the plugin in **Release** (see _Development_ below).
3. Place the built DLL here (create the folder if needed):
   ```
   Valheim/BepInEx/plugins/StreamBridge/StreamBridge.dll
   ```
4. (First run) Launch Valheim once to generate:
   ```
   Valheim/BepInEx/StreamBridge/StreamBridge.cfg
   ```
5. Optional: put the developer tester page somewhere handy and open it in a browser:
   ```
   repo/CommandsTester.html
   ```

---

## Configuration

File: `BepInEx/StreamBridge/StreamBridge.cfg` (auto-created)

| Section | Key            | Default | Purpose |<br>
|---------|----------------|---------|---------|<br>
| Network | **Port**       | 28575   | Local HTTP port (127.0.0.1 only) |<br>
| Limits  | **MaxSpawned** | 50      | Max spawn count per command |<br>
| Limits  | **MaxDelay**   | 30      | Max stagger delay (seconds) |<br>
| Limits  | **MaxTTL**     | 600     | Max time-to-live (seconds) |<br>
| Limits  | **MaxStars**   | 2       | Max mob star level (0..2) |<br>
| UI      | **Notify**     | true    | Show MessageHud toasts |<br>

> ⚠️ Rate-limiting/whitelisting is intentionally out of scope. If you proxy/forward the port, you must handle access control.

---

## API

**Endpoint**:  
`POST http://127.0.0.1:<Port>/command`  
`OPTIONS` preflight is supported (CORS).

**Request**:
```jsonc
{
  "commands": [
    {
      "id": "c1",            // optional string, echoed back
      "type": "item.give" | "effect.apply" |
              "spawn.front" | "spawn.ring" | "spawn.line" |
              "spawn.arc"   | "spawn.wedge"| "spawn.grid" |
              "spawn.scatter" | "spawn.rain",
      "payload": { /* see below */ }
    }
  ]
}
```

**Response**:
```jsonc
{
  "ok": true,
  "results": [
    { "id": "c1", "type": "item.give", "ok": true, "queued": true, "meta": { /* clamped values etc. */ } },
    { "id": "c2", "type": "spawn.ring", "ok": false, "error": "validation_failed", "message": "payload.prefab required" }
  ]
}
```

### Items — `type: "item.give"`
```jsonc
"payload": {
  "prefab": "Wood",   // required
  "amount": 25,       // >= 1 (default 1)
  "quality": 1,       // >= 1 (default 1)
  "variant": 0        // >= 0 (default 0)
}
```

### Effects — `type: "effect.apply"`
```jsonc
"payload": {
  "name": "Rested",   // required (status effect name)
  "duration": 20      // seconds; 0 = default; clamped [1..MaxTTL]
}
```

### Spawns — `type: "spawn.*"` (common fields)
```jsonc
"payload": {
  "prefab": "Skeleton", // required, exact prefab name
  "stars": 1,           // 0..2 (maps to levels 1..3)
  "delay": 0.25,        // seconds (0..MaxDelay), default 0
  "ttl": 20             // seconds (0..MaxTTL), default 0 = no auto-despawn
}
```

Patterns:

- **spawn.front** — `{ "count": 5, "distance": 8,  "delay": 0.2,  "ttl": 20 }`
- **spawn.ring**  — `{ "count": 10,"radius": 10, "delay": 0.25, "ttl": 30 }`
- **spawn.line**  — `{ "count": 8, "spacing": 3, "distance": 15, "delay": 0.15, "ttl": 15 }`
- **spawn.arc**   — `{ "count": 6, "radius": 8,  "spanDeg": 120, "delay": 0.15, "ttl": 20 }`
- **spawn.wedge** — `{ "count": 9, "spacing": 3, "distance": 10, "delay": 0.1,  "ttl": 20 }`
- **spawn.grid**  — `{ "rows": 3, "cols": 4, "spacing": 3, "distance": 12, "delay": 0.1, "ttl": 20 }`
- **spawn.scatter** — `{ "count": 20, "radius": 12, "distance": 10, "delay": 0.05, "ttl": 8 }`
- **spawn.rain**  — `{ "count": 12, "radius": 10, "height": 15, "delay": 0.25, "ttl": 20 }`

> Grounded patterns (**front, ring, line, arc, wedge, grid, scatter**) snap to terrain and run safety checks (slope/headroom/clear radius) and are **blocked in interiors/dungeons**. **rain** spawns above and lets entities fall.

---

## Examples

Minimal:
```bash
curl -X POST "http://127.0.0.1:28575/command" \
  -H "Content-Type: application/json" \
  -d '{ "commands":[
        {"id":"c1","type":"item.give","payload":{"prefab":"Wood","amount":25}},
        {"id":"c2","type":"effect.apply","payload":{"name":"Wet","duration":20}}
      ] }'
```

Waves:
```bash
curl -X POST "http://127.0.0.1:28575/command" \
  -H "Content-Type: application/json" \
  -d '{ "commands":[
        {"id":"wave1","type":"spawn.ring","payload":{"prefab":"Skeleton","stars":1,"count":6,"radius":10,"delay":0.25,"ttl":20}},
        {"id":"wave2","type":"spawn.line","payload":{"prefab":"Draugr","stars":2,"count":8,"spacing":3,"distance":15,"delay":0.15,"ttl":15}}
      ] }'
```

PowerShell:
```powershell
$body = @{
  commands = @(
    @{ id="c1"; type="spawn.grid"; payload=@{ prefab="Greydwarf"; stars=0; rows=3; cols=4; spacing=3; distance=12; delay=0.1; ttl=20 } },
    @{ id="c2"; type="item.give";   payload=@{ prefab="Resin"; amount=20; quality=1; variant=0 } }
  )
} | ConvertTo-Json -Depth 6
iwr "http://127.0.0.1:28575/command" -Method POST -ContentType "application/json" -Body $body
```

---

# Dev Tester


Included page: **`CommandsTester.html`**

- Build commands via dropdowns for items/effects/spawns
- Reorder/duplicate/delete commands
- Copy JSON or send directly (CORS-enabled server)
- Inline response viewer

Open the file directly in your browser and set the endpoint if you changed the port.

---

## Project Layout

```
/BridgePlugin.cs           # BepInEx entry + HTTP server + config + routing + CORS
/SpawnService.cs           # Spawn patterns & clamping
/SpawnSafety.cs            # Ground projection, slope/headroom checks, interior blocking
/ItemService.cs            # Inventory add/drop, stack/quality/variant
/EffectService.cs          # Apply effects by name, optional duration
/CommandsTester.html       # Local developer/testing UI
/StreamBridge-User-Guide.md# End-user documentation
/SPAWNABLE-GUIDE.md        # Spawnable prefabs & API quick-ref
```

---

## Development

- Open the solution/project in **Visual Studio 2022** (or equivalent).
- Target the same .NET profile you use for Valheim modding (BepInEx 5 typically uses .NET Framework 4.x on Windows).
- Reference:
  - `BepInEx.dll` (and dependencies)
  - `HarmonyLib.dll`
  - `UnityEngine` assemblies used by Valheim
  - `Newtonsoft.Json.dll`
- Recommended: set references to **Copy Local = false**, and ensure your build output path lands in `BepInEx/plugins/StreamBridge/`.
- Build **Release** and start Valheim.

---

## Troubleshooting

- **No response / blocked by browser** → Ensure CORS preflight (OPTIONS) gets 204 and `Access-Control-Allow-Origin: *` is present. Use the included `BridgePlugin` with the CORS helper.
- **`not_host`** errors → You must be the host/server (single-player counts as host).
- **Prefab/Effect not found** → Use exact, case-sensitive names from your game/mod set.
- **Grounded spawn rejected** → The safety checks reject steep slopes, blocked headroom, or interiors; try different terrain or use smaller counts/radii (or use `spawn.rain` outdoors).
- **Config not found** → Launch Valheim once with the plugin loaded to auto-generate it in `BepInEx/StreamBridge/`.

---

## Contributing

PRs and issues welcome. Please keep changes modular (services) and document any new payload fields in the **User Guide**.

---

## License


MIT © 2025 StreamBridge contributors — see [LICENSE-MIT.txt](LICENSE-MIT.txt) for full text.

> This license applies to the code in this repository. Valheim assets, names, and trademarks are property of their respective owners.

---

© 2025 StreamBridge contributors
