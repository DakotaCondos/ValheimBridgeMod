using BepInEx;
using BepInEx.Logging;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimBridgeMod
{
    public sealed class SpawnService
    {
        private readonly BaseUnityPlugin _host;
        private readonly ManualLogSource _log;

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

                _host.StartCoroutine(SpawnSequence(prefab, list, delaySec, ttlSec, stars));
                _log.LogInfo($"SpawnFront: {count}x {prefabName} stars={stars} dist={distance} delay={delaySec}s ttl={ttlSec}s");
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"StreamBridge: Front {count} {prefabName}(s)");
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

                _host.StartCoroutine(SpawnSequence(prefab, list, delaySec, ttlSec, stars));
                _log.LogInfo($"SpawnRing: {count}x {prefabName} stars={stars} r={radius}, delay {delaySec}s ttl {ttlSec}s");
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"StreamBridge: Ring {count} {prefabName}(s)");
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

                _host.StartCoroutine(SpawnSequence(prefab, list, delaySec, ttlSec, stars));
                _log.LogInfo($"SpawnLine: {count}x {prefabName} stars={stars} spacing={spacing} dist={distance} delay {delaySec}s ttl {ttlSec}s");
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"StreamBridge: Line {count} {prefabName}(s)");
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

                _host.StartCoroutine(SpawnSequence(prefab, list, delaySec, ttlSec, stars));
                _log.LogInfo($"SpawnArc: {count}x {prefabName} stars={stars} r={radius} span={spanDeg} delay {delaySec}s ttl {ttlSec}s");
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"StreamBridge: Arc {count} {prefabName}(s)");
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

                _host.StartCoroutine(SpawnSequence(prefab, list, delaySec, ttlSec, stars));
                _log.LogInfo($"SpawnWedge: {count}x {prefabName} stars={stars} spacing={spacing} startDist={distance} delay {delaySec}s ttl {ttlSec}s");
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"StreamBridge: Wedge {count} {prefabName}(s)");
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

                _host.StartCoroutine(SpawnSequence(prefab, list, delaySec, ttlSec, stars));
                _log.LogInfo($"SpawnGrid: {rows}x{cols} {prefabName} stars={stars} spacing={spacing} dist={distance} placed={placed} delay {delaySec}s ttl {ttlSec}s");
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

                _host.StartCoroutine(SpawnSequence(prefab, list, delaySec, ttlSec, stars));
                _log.LogInfo($"SpawnScatter: {count}x {prefabName} stars={stars} r={radius} dist={distance} delay {delaySec}s ttl {ttlSec}s");
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"StreamBridge: Scatter {count} {prefabName}(s)");
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

                _host.StartCoroutine(SpawnSequence(prefab, list, delaySec, ttlSec, stars));
                _log.LogInfo($"SpawnRain: {count}x {prefabName} stars={stars} r={radius} h={height} delay {delaySec}s ttl {ttlSec}s");
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"StreamBridge: Rain {count} {prefabName}(s)");
            }
            catch (Exception e) { _log.LogError(e); MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"StreamBridge: Spawn error {e.GetType().Name}"); }
        }
    }
}
