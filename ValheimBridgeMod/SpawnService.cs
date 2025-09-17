using BepInEx;
using BepInEx.Logging;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.IO;


namespace ValheimBridgeMod
{
    public sealed class SpawnService
    {
        private readonly BaseUnityPlugin _host;
        private readonly ManualLogSource _log;
        private readonly SpawnSafety _safety;

        // Limits / clamps (public so BridgePlugin can use them in route parsing)
        public const int MAX_SPAWN = 50;
        public const float MAX_DELAY = 30f;
        public const float MAX_TTL = 600f;
        public const int MAX_STARS = 2;

        public static int ClampCount(int c) => Math.Min(Math.Max(1, c), MAX_SPAWN);
        public static float ClampDelay(float s) => Mathf.Clamp(s, 0f, MAX_DELAY);
        public static float ClampTTL(float s) => Mathf.Clamp(s, 0f, MAX_TTL);
        public static int ClampStars(int s) => Mathf.Clamp(s, 0, MAX_STARS);

        private struct SpawnPoint
        {
            public Vector3 Pos; public Quaternion Rot;
            public SpawnPoint(Vector3 p, Quaternion r) { Pos = p; Rot = r; }
        }

        public SpawnService(BaseUnityPlugin host, ManualLogSource log)
        {
            _host = host; _log = log;
            _safety = new SpawnSafety(log);
        }

        // -------- Core helpers --------

        private bool ResolveSpawnContext(string prefabName, out Player player, out GameObject prefab)
        {
            player = Player.m_localPlayer; prefab = null;

            if (player == null)
            {
                _log.LogWarning("No local player");
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, "StreamBridge: No local player");
                return false;
            }

            if (!ZNet.instance || !ZNet.instance.IsServer())
            {
                _log.LogWarning("Spawn denied: not server/host");
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, "StreamBridge: Host only to spawn enemies");
                return false;
            }

            if (IsInDungeonByEnv())
            {
                _log.LogInfo("Spawn denied: interior/dungeon environment");
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                    "StreamBridge: Spawning is disabled in dungeons/interiors");
                return false;
            }

            var zn = ZNetScene.instance;
            if (zn == null)
            {
                _log.LogWarning("ZNetScene is null");
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, "StreamBridge: ZNetScene is null");
                return false;
            }

            prefab = zn.GetPrefab(prefabName);
            if (prefab == null)
            {
                _log.LogWarning($"Prefab not found: {prefabName}");
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"StreamBridge: Prefab not found '{prefabName}'");
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"StreamBridge: Prefab not found '{prefabName}'");
                return false;
            }
            return true;
        }

        private IEnumerator DestroyAfter(GameObject go, float ttl)
        {
            ttl = ClampTTL(ttl);
            if (ttl <= 0f || !go) yield break;
            yield return new WaitForSeconds(ttl);
            try
            {
                if (!go) yield break;
                var z = go.GetComponent<ZNetView>();
                if (z && ZNet.instance && ZNet.instance.IsServer())
                    ZNetScene.instance?.Destroy(go);
                else
                    UnityEngine.Object.Destroy(go);
            }
            catch (Exception e) { _log.LogError($"DestroyAfter error: {e}"); }
        }

        // Spawns one-by-one with delay + sets level + optional TTL
        private IEnumerator SpawnSequence(GameObject prefab, List<SpawnPoint> points, float delay, float ttl, int stars)
        {
            delay = ClampDelay(delay);
            ttl = ClampTTL(ttl);
            int level = ClampStars(stars) + 1; // 0★ -> level 1, 1★ -> 2, etc.

            for (int i = 0; i < points.Count; i++)
            {
                var sp = points[i];
                var inst = UnityEngine.Object.Instantiate(prefab, sp.Pos, sp.Rot);

                try
                {
                    var ch = inst.GetComponent<Character>();
                    if (ch != null) ch.SetLevel(level);
                }
                catch (Exception e) { _log.LogWarning($"SetLevel failed on {prefab.name}: {e.Message}"); }

                if (ttl > 0f) _host.StartCoroutine(DestroyAfter(inst, ttl));

                if (delay > 0f && i < points.Count - 1)
                    yield return new WaitForSeconds(delay);
                else
                    yield return null;
            }
        }

        // Deterministic: snap Y to ground first for grounded spawns, then validate.
        // Jitter is only a repair path; each jitter resnaps to ground.
        private List<SpawnPoint> FilterSafePoints(GameObject prefab, List<SpawnPoint> candidates, Vector3 playerPos, SpawnSafety.Options opt)
        {
            if (opt == null) opt = new SpawnSafety.Options();
            var safe = new List<SpawnPoint>(candidates.Count);

            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];

                // --- 1) snap to ground if required (no randomness) ---
                Vector3 basePos = c.Pos;
                Vector3 normal;
                if (opt.RequireGrounded)
                {
                    if (!_safety.SnapXZToGround(ref basePos, out normal, opt))
                        goto JITTER; // no ground directly beneath — try repair

                    // optional slope gate; relax MaxSlopeDeg if needed
                    if (Vector3.Angle(normal, Vector3.up) > opt.MaxSlopeDeg)
                        goto JITTER;
                }

                // --- 2) clearance & headroom at the snapped point ---
                if (HasClearance(prefab, basePos, opt) && !IsBlockedAbove(basePos, opt.Headroom)
                    && Vector3.Distance(basePos, playerPos) >= opt.MinDistFromPlayer)
                {
                    var look = (playerPos - basePos); look.y = 0f;
                    var rot = look.sqrMagnitude > 0.001f ? Quaternion.LookRotation(look.normalized) : c.Rot;
                    safe.Add(new SpawnPoint(basePos, rot));
                    continue;
                }

            // --- 3) repair path: lateral jitter + re-snap each attempt ---
            JITTER:
                bool fixedIt = false;
                for (int t = 0; t < opt.MaxAdjustTries; t++)
                {
                    var rnd = UnityEngine.Random.insideUnitCircle * opt.JitterRadius;
                    var tryPos = new Vector3(c.Pos.x + rnd.x, c.Pos.y, c.Pos.z + rnd.y);

                    if (opt.RequireGrounded)
                    {
                        if (!_safety.SnapXZToGround(ref tryPos, out normal, opt)) continue;
                        if (Vector3.Angle(normal, Vector3.up) > opt.MaxSlopeDeg) continue;
                    }

                    if (HasClearance(prefab, tryPos, opt) && !IsBlockedAbove(tryPos, opt.Headroom)
                        && Vector3.Distance(tryPos, playerPos) >= opt.MinDistFromPlayer)
                    {
                        var look2 = (playerPos - tryPos); look2.y = 0f;
                        var rot2 = look2.sqrMagnitude > 0.001f ? Quaternion.LookRotation(look2.normalized) : c.Rot;
                        safe.Add(new SpawnPoint(tryPos, rot2));
                        fixedIt = true;
                        break;
                    }
                }

                if (!fixedIt) _log.LogWarning("SpawnSafety: dropped an unsafe candidate");
            }

            return safe;
        }

        private bool IsBlockedAbove(Vector3 pos, float headroom)
        {
            return Physics.Raycast(pos + Vector3.up * 0.05f, Vector3.up, headroom, ~0, QueryTriggerInteraction.Ignore);
        }

        // Capsule clearance that starts above ground so terrain itself isn’t treated as a blocker
        private bool HasClearance(GameObject prefab, Vector3 groundPos, SpawnSafety.Options opt)
        {
            float radius, height;
            GetApproxCapsule(prefab, out radius, out height);

            // Respect configured lateral padding
            radius = Mathf.Max(radius, opt.ClearanceRadius);

            var bottom = groundPos + Vector3.up * (radius + 0.02f);
            var top = bottom + Vector3.up * Mathf.Max(0.05f, height - 2f * radius);

            return !Physics.CheckCapsule(bottom, top, radius, ~0, QueryTriggerInteraction.Ignore);
        }

        private void GetApproxCapsule(GameObject prefab, out float radius, out float height)
        {
            var cap = prefab.GetComponentInChildren<CapsuleCollider>();
            if (cap != null)
            {
                radius = Mathf.Max(0.25f, cap.radius);
                height = Mathf.Max(radius * 2f, cap.height);
                return;
            }

            var col = prefab.GetComponentInChildren<Collider>();
            if (col != null)
            {
                var b = col.bounds;
                radius = Mathf.Max(0.25f, Mathf.Min(b.extents.x, b.extents.z));
                height = Mathf.Max(radius * 2f, b.size.y);
                return;
            }

            radius = 0.5f;
            height = 2.0f;
        }

        private bool SpawnSafeOrNotify(GameObject prefab, List<SpawnPoint> safe, float delaySec, float ttlSec, int stars, string patternName, string info)
        {
            if (safe.Count == 0)
            {
                _log.LogWarning($"Spawn {patternName}: all candidates rejected by safety");
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"StreamBridge: No safe positions for {patternName}");
                return false;
            }

            _host.StartCoroutine(SpawnSequence(prefab, safe, delaySec, ttlSec, stars));
            _log.LogInfo($"Spawn{patternName}: spawning {safe.Count} (safe) {info}");
            return true;
        }

        private bool IsInDungeonByEnv()
        {
            var envMan = EnvMan.instance;
            if (envMan == null) return false;

            var env = envMan.GetCurrentEnvironment();
            if (env == null) return false;

            var name = env.m_name;
            if (string.IsNullOrEmpty(name)) return false;

            // Be resilient to naming differences
            var lower = name.ToLowerInvariant();
            if (lower.Contains("crypt") || lower.Contains("cave") || lower.Contains("mine"))
                return true;

            // Defensive exacts (common interiors)
            if (name == "Crypt" || name == "SunkenCrypt" || name == "Caves" || name == "InfectedMine" || name == "InfestedMine" || name == "TrollCave")
                return true;

            return false;
        }


        // -------- Patterns --------

        public void TrySpawnFront(string prefabName, int stars, int count, float distance, float delaySec, float ttlSec)
        {
            try
            {
                if (!ResolveSpawnContext(prefabName, out var player, out var prefab)) return;
                var origin = player.transform.position;
                var forward = player.transform.forward;

                var list = new List<SpawnPoint>(count);
                for (int i = 0; i < count; i++)
                {
                    var pos = origin + forward * distance + new Vector3(UnityEngine.Random.Range(-1f, 1f), 0, UnityEngine.Random.Range(-1f, 1f));
                    var rot = Quaternion.LookRotation((origin - pos).normalized);
                    list.Add(new SpawnPoint(pos, rot));
                }

                var opt = new SpawnSafety.Options
                {
                    RequireGrounded = true,
                    AllowUnderwater = true,
                    ClearanceRadius = 0.75f,
                    Headroom = 2.5f,
                    MinDistFromPlayer = 1.5f
                };
                var safe = FilterSafePoints(prefab, list, origin, opt);

                if (SpawnSafeOrNotify(prefab, safe, delaySec, ttlSec, stars, "Front", $"stars={stars} dist={distance} delay={delaySec}s ttl={ttlSec}s"))
                    MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"StreamBridge: Front {safe.Count} {prefabName}(s)");
            }
            catch (Exception e) { _log.LogError(e); MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"StreamBridge: Spawn error {e.GetType().Name}"); }
        }

        public void TrySpawnRing(string prefabName, int stars, int count, float radius, float delaySec, float ttlSec)
        {
            try
            {
                if (!ResolveSpawnContext(prefabName, out var player, out var prefab)) return;
                var center = player.transform.position;

                var list = new List<SpawnPoint>(count);
                for (int i = 0; i < count; i++)
                {
                    float angleDeg = (i / (float)count) * 360f;
                    var dir = Quaternion.Euler(0f, angleDeg, 0f) * Vector3.forward;
                    var pos = center + dir * radius;
                    var rot = Quaternion.LookRotation((center - pos).normalized);
                    list.Add(new SpawnPoint(pos, rot));
                }

                var opt = new SpawnSafety.Options
                {
                    RequireGrounded = true,
                    AllowUnderwater = true,
                    ClearanceRadius = 0.75f,
                    Headroom = 2.5f,
                    MinDistFromPlayer = 1.5f
                };
                var safe = FilterSafePoints(prefab, list, center, opt);

                if (SpawnSafeOrNotify(prefab, safe, delaySec, ttlSec, stars, "Ring", $"stars={stars} r={radius} delay={delaySec}s ttl={ttlSec}s"))
                    MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"StreamBridge: Ring {safe.Count} {prefabName}(s)");
            }
            catch (Exception e) { _log.LogError(e); MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"StreamBridge: Spawn error {e.GetType().Name}"); }
        }

        public void TrySpawnLine(string prefabName, int stars, int count, float spacing, float distance, float delaySec, float ttlSec)
        {
            try
            {
                if (!ResolveSpawnContext(prefabName, out var player, out var prefab)) return;
                var posPlayer = player.transform.position;
                var forward = player.transform.forward;
                var right = player.transform.right;

                var center = posPlayer + forward * distance;
                float start = -0.5f * (count - 1) * spacing;

                var list = new List<SpawnPoint>(count);
                for (int i = 0; i < count; i++)
                {
                    float offset = start + i * spacing;
                    var pos = center + right * offset;
                    var rot = Quaternion.LookRotation((posPlayer - pos).normalized);
                    list.Add(new SpawnPoint(pos, rot));
                }

                var opt = new SpawnSafety.Options
                {
                    RequireGrounded = true,
                    AllowUnderwater = true,
                    ClearanceRadius = 0.75f,
                    Headroom = 2.5f,
                    MinDistFromPlayer = 1.5f
                };
                var safe = FilterSafePoints(prefab, list, posPlayer, opt);

                if (SpawnSafeOrNotify(prefab, safe, delaySec, ttlSec, stars, "Line", $"stars={stars} spacing={spacing} dist={distance} delay={delaySec}s ttl={ttlSec}s"))
                    MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"StreamBridge: Line {safe.Count} {prefabName}(s)");
            }
            catch (Exception e) { _log.LogError(e); MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"StreamBridge: Spawn error {e.GetType().Name}"); }
        }

        public void TrySpawnArc(string prefabName, int stars, int count, float radius, float spanDeg, float delaySec, float ttlSec)
        {
            try
            {
                if (!ResolveSpawnContext(prefabName, out var player, out var prefab)) return;
                var center = player.transform.position;
                var list = new List<SpawnPoint>(count);
                var half = spanDeg * 0.5f;

                for (int i = 0; i < count; i++)
                {
                    float t = (count <= 1) ? 0f : (i / (float)(count - 1));
                    float yaw = -half + t * spanDeg;
                    var dir = Quaternion.Euler(0f, yaw, 0f) * player.transform.forward;
                    var pos = center + dir * radius;
                    var rot = Quaternion.LookRotation((center - pos).normalized);
                    list.Add(new SpawnPoint(pos, rot));
                }

                var opt = new SpawnSafety.Options
                {
                    RequireGrounded = true,
                    AllowUnderwater = true,
                    ClearanceRadius = 0.75f,
                    Headroom = 2.5f,
                    MinDistFromPlayer = 1.5f
                };
                var safe = FilterSafePoints(prefab, list, center, opt);

                if (SpawnSafeOrNotify(prefab, safe, delaySec, ttlSec, stars, "Arc", $"stars={stars} r={radius} span={spanDeg} delay={delaySec}s ttl={ttlSec}s"))
                    MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"StreamBridge: Arc {safe.Count} {prefabName}(s)");
            }
            catch (Exception e) { _log.LogError(e); MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"StreamBridge: Spawn error {e.GetType().Name}"); }
        }

        public void TrySpawnWedge(string prefabName, int stars, int count, float spacing, float distance, float delaySec, float ttlSec)
        {
            try
            {
                if (!ResolveSpawnContext(prefabName, out var player, out var prefab)) return;
                var origin = player.transform.position;
                var fwd = player.transform.forward;
                var right = player.transform.right;

                var list = new List<SpawnPoint>(count);
                int placed = 0;
                for (int row = 0; placed < count; row++)
                {
                    int rowCount = row + 1;
                    float depth = distance + row * spacing;
                    float start = -0.5f * (rowCount - 1) * spacing;

                    for (int i = 0; i < rowCount && placed < count; i++)
                    {
                        float offset = start + i * spacing;
                        var pos = origin + fwd * depth + right * offset;
                        var rot = Quaternion.LookRotation((origin - pos).normalized);
                        list.Add(new SpawnPoint(pos, rot));
                        placed++;
                    }
                }

                var opt = new SpawnSafety.Options
                {
                    RequireGrounded = true,
                    AllowUnderwater = true,
                    ClearanceRadius = 0.75f,
                    Headroom = 2.5f,
                    MinDistFromPlayer = 1.5f
                };
                var safe = FilterSafePoints(prefab, list, origin, opt);

                if (SpawnSafeOrNotify(prefab, safe, delaySec, ttlSec, stars, "Wedge", $"stars={stars} spacing={spacing} startDist={distance} delay={delaySec}s ttl={ttlSec}s"))
                    MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"StreamBridge: Wedge {safe.Count} {prefabName}(s)");
            }
            catch (Exception e) { _log.LogError(e); MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"StreamBridge: Spawn error {e.GetType().Name}"); }
        }

        public void TrySpawnGrid(string prefabName, int stars, int rows, int cols, float spacing, float distance, float delaySec, float ttlSec)
        {
            try
            {
                if (!ResolveSpawnContext(prefabName, out var player, out var prefab)) return;
                var origin = player.transform.position;
                var fwd = player.transform.forward;
                var right = player.transform.right;

                int total = Math.Min(rows * cols, MAX_SPAWN);
                var center = origin + fwd * distance;
                var list = new List<SpawnPoint>(total);
                int placed = 0;

                for (int r = 0; r < rows && placed < total; r++)
                {
                    float z = (r - (rows - 1) * 0.5f) * spacing;
                    for (int c = 0; c < cols && placed < total; c++)
                    {
                        float x = (c - (cols - 1) * 0.5f) * spacing;
                        var pos = center + right * x + fwd * z;
                        var rot = Quaternion.LookRotation((origin - pos).normalized);
                        list.Add(new SpawnPoint(pos, rot));
                        placed++;
                    }
                }

                var opt = new SpawnSafety.Options
                {
                    RequireGrounded = true,
                    AllowUnderwater = true,
                    ClearanceRadius = 0.75f,
                    Headroom = 2.5f,
                    MinDistFromPlayer = 1.5f
                };
                var safe = FilterSafePoints(prefab, list, origin, opt);

                if (SpawnSafeOrNotify(prefab, safe, delaySec, ttlSec, stars, "Grid", $"stars={stars} spacing={spacing} dist={distance} placed={safe.Count} delay={delaySec}s ttl={ttlSec}s"))
                    MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"StreamBridge: Grid {rows}x{cols} {prefabName}");
            }
            catch (Exception e) { _log.LogError(e); MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"StreamBridge: Spawn error {e.GetType().Name}"); }
        }

        public void TrySpawnScatter(string prefabName, int stars, int count, float radius, float distance, float delaySec, float ttlSec)
        {
            try
            {
                if (!ResolveSpawnContext(prefabName, out var player, out var prefab)) return;
                var origin = player.transform.position;
                var center = origin + player.transform.forward * distance;

                var list = new List<SpawnPoint>(count);
                for (int i = 0; i < count; i++)
                {
                    var v2 = UnityEngine.Random.insideUnitCircle * radius;
                    var pos = center + new Vector3(v2.x, 0f, v2.y);
                    var rot = Quaternion.LookRotation((origin - pos).normalized);
                    list.Add(new SpawnPoint(pos, rot));
                }

                var opt = new SpawnSafety.Options
                {
                    RequireGrounded = true,
                    AllowUnderwater = true,
                    ClearanceRadius = 0.75f,
                    Headroom = 2.5f,
                    MinDistFromPlayer = 1.5f
                };
                var safe = FilterSafePoints(prefab, list, origin, opt);

                if (SpawnSafeOrNotify(prefab, safe, delaySec, ttlSec, stars, "Scatter", $"stars={stars} r={radius} dist={distance} delay={delaySec}s ttl={ttlSec}s"))
                    MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"StreamBridge: Scatter {safe.Count} {prefabName}(s)");
            }
            catch (Exception e) { _log.LogError(e); MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"StreamBridge: Spawn error {e.GetType().Name}"); }
        }

        public void TrySpawnRain(string prefabName, int stars, int count, float radius, float height, float delaySec, float ttlSec)
        {
            try
            {
                if (!ResolveSpawnContext(prefabName, out var player, out var prefab)) return;
                var origin = player.transform.position;
                var center = origin;

                var list = new List<SpawnPoint>(count);
                for (int i = 0; i < count; i++)
                {
                    var v2 = UnityEngine.Random.insideUnitCircle * radius;
                    var pos = center + new Vector3(v2.x, height, v2.y);
                    var lookTarget = new Vector3(origin.x, pos.y, origin.z);
                    var dir = (lookTarget - pos).normalized;
                    var rot = dir.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(dir) : Quaternion.identity;
                    list.Add(new SpawnPoint(pos, rot));
                }

                // Airborne: keep RequireGrounded=false
                var opt = new SpawnSafety.Options
                {
                    RequireGrounded = false,
                    AllowUnderwater = true,
                    ClearanceRadius = 0.75f,
                    Headroom = 2.5f,
                    MinDistFromPlayer = 1.5f
                };
                var safe = FilterSafePoints(prefab, list, origin, opt);

                if (SpawnSafeOrNotify(prefab, safe, delaySec, ttlSec, stars, "Rain", $"stars={stars} r={radius} h={height} delay={delaySec}s ttl={ttlSec}s"))
                    MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"StreamBridge: Rain {safe.Count} {prefabName}(s)");
            }
            catch (Exception e) { _log.LogError(e); MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"StreamBridge: Spawn error {e.GetType().Name}"); }
        }


        // ---------------- Utilities ----------------
        public void ExportSpawnNamesCsv(string mode /* "characters" or "all" */)
        {
            try
            {
                var zn = ZNetScene.instance;
                if (zn == null || zn.m_prefabs == null)
                {
                    _log.LogWarning("Export: ZNetScene or prefabs list not ready");
                    MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, "StreamBridge: Prefab list not ready");
                    return;
                }

                bool onlyCharacters = string.IsNullOrEmpty(mode)
                    || mode.Equals("characters", StringComparison.OrdinalIgnoreCase);

                // Collect rows: (prefab, display)
                var rows = new List<Tuple<string, string>>(zn.m_prefabs.Count);

                for (int i = 0; i < zn.m_prefabs.Count; i++)
                {
                    var go = zn.m_prefabs[i];
                    if (!go) continue;

                    string prefab = go.name;
                    if (string.IsNullOrEmpty(prefab)) continue;

                    string display = string.Empty;

                    var ch = go.GetComponent<Character>();
                    if (ch != null)
                    {
                        // Prefer Character.m_name; localize if it’s a key like "$enemy_greydwarf"
                        display = ch.m_name;
                        if (display.IsNullOrWhiteSpace())
                        {
                            display = string.Empty;
                        }
                    }
                    else if (onlyCharacters)
                    {
                        // Skip non-characters in "characters" mode
                        continue;
                    }
                    else
                    {
                        // "all" mode: leave display empty (not a Character)
                        display = string.Empty;
                    }

                    rows.Add(Tuple.Create(prefab, display));
                }

                // Sort by prefab (stable, predictable)
                rows.Sort((a, b) => string.Compare(a.Item1, b.Item1, StringComparison.Ordinal));

                // Write CSV to BepInEx/StreamBridge
                string root = Paths.BepInExRootPath; // BepInEx path
                if (string.IsNullOrEmpty(root)) root = Directory.GetCurrentDirectory();
                string outDir = Path.Combine(root, "StreamBridge");
                Directory.CreateDirectory(outDir);

                string file = Path.Combine(outDir, onlyCharacters
                    ? "spawnable_prefabs-characters.csv"
                    : "spawnable_prefabs-all.csv");

                using (var sw = new StreamWriter(file, false, new System.Text.UTF8Encoding(true)))
                {
                    sw.WriteLine("prefab,display_name");
                    for (int i = 0; i < rows.Count; i++)
                    {
                        var p = rows[i].Item1 ?? "";
                        var d = rows[i].Item2 ?? "";
                        sw.WriteLine(CsvEsc(p) + "," + CsvEsc(d));
                    }
                }

                _log.LogInfo($"Exported {rows.Count} prefab(s) to {file}");
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                    $"StreamBridge: Exported {rows.Count} names\n{file}");
            }
            catch (Exception e)
            {
                _log.LogError($"ExportSpawnNamesCsv error: {e}");
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, "StreamBridge: Export failed");
            }
        }

        // Minimal CSV escaper for commas/quotes/newlines
        private static string CsvEsc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            bool needQuotes = s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
            if (s.IndexOf('"') >= 0) s = s.Replace("\"", "\"\"");
            return needQuotes ? "\"" + s + "\"" : s;
        }

    }
}