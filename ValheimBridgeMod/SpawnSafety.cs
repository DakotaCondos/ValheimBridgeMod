using BepInEx.Logging;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimBridgeMod
{
    public sealed class SpawnSafety
    {
        private readonly ManualLogSource _log;

        public SpawnSafety(ManualLogSource log) => _log = log;

        // Tunables (defaults are conservative; tweak to taste)
        public class Options
        {
            public float GroundRayUp = 40f;       // how high above candidate to start ray
            public float GroundRayDown = 120f;    // how far to raycast down
            public float MaxSlopeDeg = 40f;       // reject steeper ground
            public float ClearanceRadius = 0.75f; // horizontal clearance around spawn (used with capsule)
            public float Headroom = 2.5f;         // vertical space above spawn
            public float MinDistFromPlayer = 1.5f;// don’t spawn inside the player
            public bool RequireGrounded = true;   // false for “rain” or flying
            public bool AllowUnderwater = true;   // toggle if you want to forbid water spawns
            public int MaxAdjustTries = 12;       // attempts to jitter/repair a bad point
            public float JitterRadius = 1.25f;    // lateral jitter per attempt
        }

        private static readonly QueryTriggerInteraction QTI = QueryTriggerInteraction.Ignore;

        // --- Deterministic snap: keep XZ, set Y to ground (slight lift). Returns hit normal.
        public bool SnapXZToGround(ref Vector3 pos, out Vector3 groundNormal, Options opt = null, int? layerMaskOverride = null)
        {
            if (opt == null) opt = new Options();
            int mask = layerMaskOverride ?? ~0;

            var start = new Vector3(pos.x, pos.y + opt.GroundRayUp, pos.z);
            var dist = opt.GroundRayUp + opt.GroundRayDown;

            RaycastHit hit;
            // SphereCast first — more forgiving on edges
            if (Physics.SphereCast(start, 0.35f, Vector3.down, out hit, dist, mask, QTI))
            {
                pos.y = hit.point.y + 0.02f;
                groundNormal = hit.normal;
                return true;
            }
            // Fallback raycast
            if (Physics.Raycast(start, Vector3.down, out hit, dist, mask, QTI))
            {
                pos.y = hit.point.y + 0.02f;
                groundNormal = hit.normal;
                return true;
            }

            groundNormal = Vector3.up;
            return false;
        }

        // Simple projection helper (kept for compatibility with earlier code)
        public bool ProjectToGround(Vector3 desired, out Vector3 groundPos, out Vector3 groundNormal, Options opt = null)
        {
            if (opt == null) opt = new Options();

            var start = desired + Vector3.up * opt.GroundRayUp;
            var total = opt.GroundRayUp + opt.GroundRayDown;

            RaycastHit hit;
            if (Physics.SphereCast(start, 0.35f, Vector3.down, out hit, total, Physics.DefaultRaycastLayers, QTI))
            {
                groundPos = hit.point + hit.normal * 0.02f;
                groundNormal = hit.normal;
                return true;
            }
            if (Physics.Raycast(start, Vector3.down, out hit, total, Physics.DefaultRaycastLayers, QTI))
            {
                groundPos = hit.point + hit.normal * 0.02f;
                groundNormal = hit.normal;
                return true;
            }

            groundPos = desired;
            groundNormal = Vector3.up;
            return false;
        }

        // Try to place a point and validate (kept for generic callers; our SpawnService uses the snapper directly)
        public bool TryMakeSafe(GameObject prefab, Vector3 desired, Vector3 playerPos, out Vector3 safePos, out Quaternion safeRot, Options opt = null)
        {
            if (opt == null) opt = new Options();
            safePos = desired;
            safeRot = Quaternion.identity;

            Vector3 pos = desired;
            Vector3 normal = Vector3.up;

            if (opt.RequireGrounded)
            {
                if (!SnapXZToGround(ref pos, out normal, opt)) return false;

                // slope check
                float slope = Vector3.Angle(normal, Vector3.up);
                if (slope > opt.MaxSlopeDeg) return false;
            }

            // water rule
            if (!opt.AllowUnderwater && IsUnderwater(pos))
                return false;

            // clearance / headroom
            if (!HasClearance(prefab, pos, opt)) return false;

            // player distance
            if (Vector3.Distance(pos, playerPos) < opt.MinDistFromPlayer) return false;

            // face toward player by default
            var look = (playerPos - pos); look.y = 0f;
            if (look.sqrMagnitude < 0.0001f) look = Vector3.forward;
            safeRot = Quaternion.LookRotation(look.normalized);
            safePos = pos;
            return true;
        }

        // Tries multiple jitters around each candidate to “repair” bad points. (Unused by our current SpawnService path)
        public List<(Vector3 pos, Quaternion rot)> MakeSafeList(GameObject prefab, List<(Vector3 pos, Quaternion rot)> candidates, Vector3 playerPos, Options opt = null)
        {
            if (opt == null) opt = new Options();
            var outList = new List<(Vector3, Quaternion)>(candidates.Count);

            foreach (var c in candidates)
            {
                if (TryMakeSafe(prefab, c.pos, playerPos, out var goodPos, out var goodRot, opt))
                {
                    outList.Add((goodPos, goodRot));
                    continue;
                }

                bool fixedIt = false;
                for (int i = 0; i < opt.MaxAdjustTries; i++)
                {
                    var rnd = UnityEngine.Random.insideUnitCircle * opt.JitterRadius;
                    var tryPos = c.pos + new Vector3(rnd.x, 0f, rnd.y);
                    if (TryMakeSafe(prefab, tryPos, playerPos, out goodPos, out goodRot, opt))
                    {
                        outList.Add((goodPos, goodRot));
                        fixedIt = true;
                        break;
                    }
                }
                if (!fixedIt)
                    _log.LogWarning("SpawnSafety: dropped an unsafe candidate");
            }

            return outList;
        }

        // ----- Helpers -----

        // Clearance as an upright capsule that starts ABOVE ground (so terrain collider isn’t counted)
        private bool HasClearance(GameObject prefab, Vector3 groundPos, Options opt)
        {
            float radius, height;
            GetApproxCapsule(prefab, out radius, out height);

            // Honor the configured ClearanceRadius if it’s larger than the collider radius
            radius = Mathf.Max(radius, opt.ClearanceRadius);

            var bottom = groundPos + Vector3.up * (radius + 0.02f);
            var top = bottom + Vector3.up * Mathf.Max(0.05f, height - 2f * radius);

            return !Physics.CheckCapsule(bottom, top, radius, ~0, QTI)
                && !Physics.Raycast(groundPos + Vector3.up * 0.05f, Vector3.up, opt.Headroom, ~0, QTI);
        }

        // Approx collider if the prefab lacks a CapsuleCollider
        private void GetApproxCapsule(GameObject prefab, out float radius, out float height)
        {
            var cap = prefab.GetComponentInChildren<CapsuleCollider>();
            if (cap)
            {
                radius = Mathf.Max(0.25f, cap.radius);
                height = Mathf.Max(radius * 2f, cap.height);
                return;
            }

            var col = prefab.GetComponentInChildren<Collider>();
            if (col)
            {
                var b = col.bounds;
                radius = Mathf.Max(0.25f, Mathf.Min(b.extents.x, b.extents.z));
                height = Mathf.Max(radius * 2f, b.size.y);
                return;
            }

            radius = 0.5f;
            height = 2.0f;
        }

        // Very simple water test; replace with Valheim water level if available
        private bool IsUnderwater(Vector3 pos)
        {
            var col = Physics.OverlapSphere(pos, 0.25f, ~0, QTI);
            foreach (var c in col)
            {
                if (!c) continue;
                if (string.Equals(c.tag, "Water", StringComparison.OrdinalIgnoreCase)) return true;
                if (c.name.IndexOf("Water", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }
    }
}
