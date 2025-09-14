using BepInEx.Logging;
using System;

namespace ValheimBridgeMod
{
    public sealed class EffectService
    {
        private readonly ManualLogSource _log;
        public EffectService(ManualLogSource log) => _log = log;

        public void TryApplyEffectGeneric(string effectName, float durationSeconds = 10f)
        {
            try
            {
                var player = Player.m_localPlayer;
                if (player == null)
                {
                    _log.LogWarning("No local player");
                    MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, "StreamBridge: No local player");
                    return;
                }

                var odb = ObjectDB.instance;
                if (odb == null)
                {
                    _log.LogWarning("ObjectDB is null");
                    MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, "StreamBridge: ObjectDB is null");
                    return;
                }

                int hash = effectName.GetStableHashCode();
                var se = odb.GetStatusEffect(hash);
                if (se == null)
                {
                    _log.LogWarning($"Status effect not found: {effectName}");
                    MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"StreamBridge: Effect not found '{effectName}'");
                    return;
                }

                var seInst = UnityEngine.Object.Instantiate(se);
                seInst.m_ttl = Math.Max(1f, durationSeconds);
                player.GetSEMan().AddStatusEffect(seInst, resetTime: true);

                _log.LogInfo($"Applied {effectName} for {durationSeconds}s");
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"StreamBridge: {effectName} {durationSeconds:0}s");
            }
            catch (Exception e)
            {
                _log.LogError(e);
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"StreamBridge: SE error {e.GetType().Name}");
            }
        }
    }
}
