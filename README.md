# StreamBridge (Valheim)

A lightweight **host-only** bridge for Valheim that listens on `localhost` and executes gameplay actions sent as **JSON** over HTTP. 
It is designed to be used by companion apps/bots during streams (e.g., chat-triggered spawns, item rewards, or temporary effects).

[Project overview](https://red-wave-026ab431e.3.azurestaticapps.net/valheim) · [Release and browser command tester](https://github.com/DakotaCondos/ValheimBridgeMod/releases/tag/v1.0.1)

---

## 📥 Download & Installation

⚠️ **Requires [BepInEx 5](https://docs.bepinex.dev/articles/user_guide/installation/index.html).**  
BepInEx 5 must be installed into your Valheim folder and the game launched once so that it creates required folders (`BepInEx\plugins`, `BepInEx\config`, `BepInEx\core`).  
This is a **one-time setup step** — both release users and developers must do this before StreamBridge will work.

➡️ [**Releases Page**](https://github.com/DakotaCondos/ValheimBridgeMod/releases)

1. Download the zip file **ValheimBridgeMod.zip** from the release assets.  
   This archive contains the `StreamBridge` plugin folder with the DLL already built.
2. Extract the contents directly into your Valheim plugins directory:
   ```
   Valheim\BepInEx\plugins\
   ```
   After extraction, you should have:
   ```
   Valheim\BepInEx\plugins\StreamBridge\StreamBridge.dll
   Valheim\BepInEx\plugins\StreamBridge\Newtonsoft.Json.dll
   Valheim\BepInEx\plugins\StreamBridge\Newtonsoft.Json.xml
   ```
3. Launch Valheim once to auto-generate:
   ```
   Valheim\BepInEx\StreamBridge\StreamBridge.cfg
   ```
4. *(Recommended)* Download **CommandsTester.html** from the release assets to quickly generate and test commands from your browser.

That’s it — you’re ready to connect local apps and/or use the included tester page.

---

## 🛠️ Development Setup

Whether you want to build from the included codebase or start your own project, you must first complete the **one-time prerequisite**:

- Install **BepInEx 5 (x64)** into your Valheim folder:  
  ```
  C:\Program Files (x86)\Steam\steamapps\common\Valheim\
  ```
- Launch Valheim once to generate required folders:  
  ```
  BepInEx\plugins\
  BepInEx\config\
  BepInEx\core\
  ```
- Install **Visual Studio 2022** with the **.NET desktop development** workload.  
- (Optional but useful) Install dnSpy/ILSpy to explore Valheim APIs.

---

### Option A: Build from this codebase

1. Clone the repository:
   ```bash
   git clone https://github.com/DakotaCondos/ValheimBridgeMod.git
   ```
2. Open the solution in Visual Studio 2022.
3. Ensure target framework is **.NET Framework 4.7.2** (compatible with Valheim + BepInEx 5).
4. References should already be set up in the project:
   - `0HarmonyLib.dll`
   - `BepInEx.dll`
   - `assembly_valheim.dll`
   - `assembly_utils.dll`
   - `UnityEngine.CoreModule.dll`
   - `UnityEngine.PhysicsModule.dll`
   - `UnityEngine.dll`
   - `Newtonsoft.Json.dll`
5. Set build output path so the DLL lands directly in your Valheim plugins folder:  
   ```
   Valheim\BepInEx\plugins\StreamBridgeMod\
   ```

6. For packaging, create a folder called `StreamBridge` with the DLL inside, zip it, and attach to a GitHub release.  
   For local testing, place the DLL under:
   ```
   Valheim\BepInEx\plugins\StreamBridge\
   ```

---

### Option B: Create a project yourself (manual setup)

0. Prerequisites: BepInEx installed + run once (as described above).

1. In Visual Studio:  
   - **File → New → Project…**  
   - Template: `Class Library (.NET Framework)`  
   - Project name: `StreamBridgeMod` (or your choice)  
   - Target framework: **.NET Framework 4.7.2**
2. Add required references manually:
   - From `<Valheim>\valheim_Data\Managed\`:  
     `assembly_valheim.dll`, `assembly_utils.dll`, `UnityEngine.dll`, `UnityEngine.CoreModule.dll`  
     (Optionally add more `UnityEngine.*` modules if you need them.)
   - From `<Valheim>\BepInEx\core\`:  
     `BepInEx.dll`, `0Harmony.dll`
   - Via NuGet: add **Newtonsoft.Json** (Copy Local = True so it ships with your DLL).
   - For all Unity/BepInEx references: set **Copy Local = False** to avoid copying them into your build output.
3. Set build output path so the DLL lands directly in your Valheim plugins folder:  
   ```
   Valheim\BepInEx\plugins\StreamBridgeMod\  
   ```
4. Build → Build Solution (`Ctrl+Shift+B`).  
   Ensure the DLL appears under `BepInEx\plugins\StreamBridgeMod\`.  
   Also ensure `Newtonsoft.Json.dll` sits next to it if not already provided.
5. Launch Valheim and check `BepInEx\LogOutput.log` — you should see:  
   ```
   Stream Bridge listening on http://127.0.0.1:28575/command
   ```

6. The rest of development is up to you!
---

## Configuration

(File: `BepInEx/StreamBridge/StreamBridge.cfg`, created automatically on first run)

| Section | Key            | Default | Purpose |
|---------|----------------|---------|---------|
| Network | **Port**       | 28575   | Local HTTP port (127.0.0.1 only) |
| Limits  | **MaxSpawned** | 50      | Max spawn count per command |
| Limits  | **MaxDelay**   | 30      | Max stagger delay (seconds) |
| Limits  | **MaxTTL**     | 600     | Max time-to-live (seconds) |
| Limits  | **MaxStars**   | 2       | Max mob star level (0..2) |
| UI      | **Notify**     | true    | Show HUD toasts |

---

## API

**Endpoint**:  
`POST http://127.0.0.1:<Port>/command`  
`OPTIONS` preflight is supported (CORS).

The body must be JSON with a `commands` array. Each command has an `id`, `type`, and `payload`.  
See the included **User Guide** for detailed examples of `item.give`, `effect.apply`, and `spawn.*` commands.

---

## Contributing

- Issues and PRs welcome.  
- Please document any new payload fields in the **User Guide**.  
- For testing during development, use `CommandsTester.html`.

---

## License

MIT © 2025 StreamBridge contributors — see [LICENSE-MIT.txt](LICENSE-MIT.txt) for full text.  
This license applies only to code in this repository. Valheim assets, names, and trademarks are property of their respective owners.

---
