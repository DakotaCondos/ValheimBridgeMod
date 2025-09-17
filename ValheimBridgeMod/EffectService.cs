using BepInEx;
using BepInEx.Logging;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimBridgeMod
{
    public sealed class EffectService
    {
        private readonly BaseUnityPlugin _host;
        private readonly ManualLogSource _log;

        // Duration clamp for generic effects
        private const float MIN_DURATION = 1f;
        private const float MAX_DURATION = 600f;

        public EffectService(BaseUnityPlugin host, ManualLogSource log)
        {
            _host = host;
            _log = log;
        }

        // ---------- small helpers (JSON + HUD) ----------
        private static int ReadInt(IDictionary<string, string> kv, string k, int def)
            => (kv != null && kv.TryGetValue(k, out var s) && int.TryParse(s, out var v)) ? v : def;

        private static float ReadFloat(IDictionary<string, string> kv, string k, float def)
            => (kv != null && kv.TryGetValue(k, out var s) &&
                float.TryParse(s, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v)) ? v : def;

        private static bool ReadBool(IDictionary<string, string> kv, string k, bool def)
        {
            if (kv == null || !kv.TryGetValue(k, out var s) || s == null) return def;
            if (bool.TryParse(s, out var b)) return b;
            if (int.TryParse(s, out var i)) return i != 0;
            var t = s.Trim().ToLowerInvariant();
            if (t == "y" || t == "yes" || t == "on" || t == "true") return true;
            if (t == "n" || t == "no" || t == "off" || t == "false") return false;
            return def;
        }

        private static string GetStr(System.Collections.Generic.IDictionary<string, string> kv, string k, string def)
            => (kv != null && kv.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) ? v : def;

        private static void Hud(string msg, bool notify)
        {
            if (!notify) return;
            MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, msg);
        }

        // ---------- PUBLIC API (original signature kept) ----------

        /// <summary>
        /// Apply a built-in status effect by its exact prefab/effect name.
        /// Unique-refresh semantics; duration clamped to [1,600]s.
        /// </summary>
        public void TryApplyEffectGeneric(string effectName, float durationSeconds = 10f)
            => TryApplyEffectGeneric(effectName, durationSeconds, notify: true);

        /// <summary>
        /// Notify-aware overload (HUD only if notify==true).
        /// </summary>
        public void TryApplyEffectGeneric(string effectName, float durationSeconds, bool notify)
        {
            try
            {
                // Defense-in-depth: host-only (BridgePlugin also checks)
                if (!ZNet.instance || !ZNet.instance.IsServer())
                {
                    _log.LogWarning("Effect denied: not server/host");
                    Hud("StreamBridge: Host only to apply effects", notify);
                    return;
                }

                var player = Player.m_localPlayer;
                if (player == null)
                {
                    _log.LogWarning("No local player");
                    Hud("StreamBridge: No local player", notify);
                    return;
                }

                var odb = ObjectDB.instance;
                if (odb == null)
                {
                    _log.LogWarning("ObjectDB is null");
                    Hud("StreamBridge: ObjectDB is null", notify);
                    return;
                }

                // exact, case-sensitive name
                int hash = effectName.GetStableHashCode();
                var se = odb.GetStatusEffect(hash);
                if (se == null)
                {
                    _log.LogWarning($"Status effect not found: {effectName}");
                    Hud($"StreamBridge: Effect not found '{effectName}'", notify);
                    return;
                }

                var ttl = Mathf.Clamp(durationSeconds, MIN_DURATION, MAX_DURATION);
                var seInst = UnityEngine.Object.Instantiate(se);
                seInst.m_ttl = ttl;

                ApplyUnique(player, seInst, effectName, notify);
                _log.LogInfo($"Applied {effectName} for {ttl:0.#}s");
            }
            catch (Exception e)
            {
                _log.LogError(e);
                Hud($"StreamBridge: SE error {e.GetType().Name}", notify);
            }
        }

        /// <summary>
        /// Adds the given effect to the player, refreshing if the same-name effect is already active.
        /// </summary>
        public void ApplyUnique(Player player, StatusEffect se, string labelForHud, bool notify)
        {
            if (!player || se == null) return;

            try
            {
                var seman = player.GetSEMan();
                // Use stable hash of effect name (Valheim style)
                int nameHash = se.name.GetStableHashCode();

                // Refresh by removing then adding so TTL and modifiers reset cleanly
                var existing = seman.GetStatusEffect(nameHash);
                if (existing != null)
                    seman.RemoveStatusEffect(nameHash, true);

                seman.AddStatusEffect(se, true);

                if (notify && !string.IsNullOrEmpty(labelForHud))
                {
                    int secs = Mathf.RoundToInt(Mathf.Max(0f, se.m_ttl));
                    Hud(secs > 0 ? $"StreamBridge: {labelForHud} ({secs}s)" : $"StreamBridge: {labelForHud}", true);
                }
            }
            catch (Exception e)
            {
                _log.LogError("ApplyUnique failed: " + e);
                Hud("StreamBridge: Effect apply failed", notify);
            }
        }

        // ---------- JSON entry points ----------

        /// <summary>
        /// JSON-friendly generic effect: body { "durationSeconds": <float>, "notify": <bool> }
        /// </summary>
        public bool TryApplyFromJson(string effectName, IDictionary<string, string> bodyKv, bool defaultNotify, out string error)
        {
            error = null;
            try
            {
                float dur = ReadFloat(bodyKv, "durationSeconds", 10f);
                bool notify = ReadBool(bodyKv, "notify", defaultNotify);
                TryApplyEffectGeneric(effectName, dur, notify);
                return true;
            }
            catch (Exception e)
            {
                _log.LogError($"TryApplyFromJson error: {e}");
                error = "server_error";
                return false;
            }
        }

        // ======= Custom Effects (JSON-only) =======

        /// <summary>
        /// JSON-only custom effects.
        /// Body (flat, keys are case-sensitive):
        /// {
        ///   "name": "MyCustom",                       // optional; default "CustomEffect"
        ///   "template": "Rested",                     // optional; clone existing effect if present
        ///   "durationSeconds": 20,                    // optional; clamped [1..600]
        ///   "stackBehavior": "unique-refresh|stack",  // optional; default "unique-refresh"
        ///   "notify": true|false,                     // optional; HUD toggle (logs always)
        ///   // optional numeric modifiers (flat) – applied if the field exists on SE_Stats:
        ///   "movementSpeed": 1.15,
        ///   "runStaminaDrain": -0.15,
        ///   "jumpStaminaUse": -0.15,
        ///   "staminaRegen": 0.50,
        ///   "healthRegen": 0.25,
        ///   "eitrRegen": 0.25
        /// }
        /// Unknown fields are ignored. We reflect only if the target field exists so this is future-proof.
        /// </summary>
        public void TryApplyCustomFromJson(
            System.Collections.Generic.IDictionary<string, string> kv,
            bool defaultNotify)
        {
            try
            {
                // Host-only (defense in depth)
                if (!ZNet.instance || !ZNet.instance.IsServer())
                {
                    _log.LogWarning("Custom effect denied: not server/host");
                    Hud("StreamBridge: Host only to apply effects", defaultNotify);
                    return;
                }

                var player = Player.m_localPlayer;
                if (!player)
                {
                    _log.LogWarning("No local player");
                    Hud("StreamBridge: No local player", defaultNotify);
                    return;
                }

                var odb = ObjectDB.instance;
                if (!odb)
                {
                    _log.LogWarning("ObjectDB is null");
                    Hud("StreamBridge: ObjectDB is null", defaultNotify);
                    return;
                }

                string name = GetStr(kv, "name", "CustomEffect");
                string template = GetStr(kv, "template", null);
                string stack = GetStr(kv, "stackBehavior", "unique-refresh");
                bool notify = ReadBool(kv, "notify", defaultNotify);
                float ttl = Mathf.Clamp(ReadFloat(kv, "durationSeconds", 10f), MIN_DURATION, MAX_DURATION);

                StatusEffect seInst = null;

                // A) Template mode: clone an existing effect if provided and found
                if (!string.IsNullOrEmpty(template))
                {
                    var tpl = odb.GetStatusEffect(template.GetStableHashCode());
                    if (tpl != null)
                    {
                        seInst = UnityEngine.Object.Instantiate(tpl);
                        // Optional cosmetic rename so it’s clear in HUD/debug (name used in hash; keep internal "name" stable)
                        seInst.m_name = string.IsNullOrEmpty(name) ? tpl.m_name : name;
                    }
                    else
                    {
                        _log.LogWarning($"Custom: template not found '{template}', falling back to stats effect");
                    }
                }

                // B) Stats mode (or fallback if template missing): create SE_Stats and apply flat modifiers if present
                if (seInst == null)
                {
                    var stats = ScriptableObject.CreateInstance<SE_Stats>();
                    stats.m_name = string.IsNullOrEmpty(name) ? "CustomStats" : name;

                    // Reflectively set any known numeric fields if they exist on this Valheim version
                    SetIfExists(stats, "m_speedModifier", ReadFloat(kv, "movementSpeed", float.NaN));
                    SetIfExists(stats, "m_runStaminaDrainModifier", ReadFloat(kv, "runStaminaDrain", float.NaN));
                    SetIfExists(stats, "m_jumpStaminaUseModifier", ReadFloat(kv, "jumpStaminaUse", float.NaN));
                    SetIfExists(stats, "m_staminaRegenMultiplier", ReadFloat(kv, "staminaRegen", float.NaN));
                    SetIfExists(stats, "m_healthRegenMultiplier", ReadFloat(kv, "healthRegen", float.NaN));
                    SetIfExists(stats, "m_eitrRegenMultiplier", ReadFloat(kv, "eitrRegen", float.NaN));

                    seInst = stats;
                }

                // TTL and apply semantics
                seInst.m_ttl = ttl;
                if (stack.Equals("stack", StringComparison.OrdinalIgnoreCase))
                {
                    player.GetSEMan().AddStatusEffect(seInst, true);
                    _log.LogInfo($"Custom effect (stack): {seInst.m_name} ttl={ttl:0.#}s");
                    Hud($"StreamBridge: {seInst.m_name} ({Mathf.RoundToInt(ttl)}s)", notify);
                }
                else
                {
                    ApplyUnique(player, seInst, seInst.m_name, notify);
                    _log.LogInfo($"Custom effect (unique-refresh): {seInst.m_name} ttl={ttl:0.#}s");
                }
            }
            catch (Exception e)
            {
                _log.LogError($"TryApplyCustomFromJson error: {e}");
                Hud("StreamBridge: Custom effect error", defaultNotify);
            }
        }




        // Set field by name only if it exists and value is a number (not NaN). Safe across Valheim versions.
        private static void SetIfExists(object target, string fieldName, float value)
        {
            if (float.IsNaN(value)) return;
            var f = target.GetType().GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (f != null && f.FieldType == typeof(float)) f.SetValue(target, value);
        }
    }
}
