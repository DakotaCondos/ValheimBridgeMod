using System;
using BepInEx.Logging;
using UnityEngine;

namespace ValheimBridgeMod
{
    public sealed class ItemService
    {
        private readonly ManualLogSource _log;

        public ItemService(ManualLogSource log)
        {
            _log = log;
        }

        /// <summary>
        /// Give an item by prefab name to the local player.
        /// Splits into valid stack sizes; drops stacks on the ground if inventory is full.
        /// </summary>
        public void TryGiveItemGeneric(string prefabName, int amount, int quality, int variant)
        {
            try
            {
                var player = Player.m_localPlayer;
                if (!player)
                {
                    _log.LogInfo("item.give: no local player");
                    MaybeToast("StreamBridge: No local player");
                    return;
                }

                var odb = ObjectDB.instance;
                if (!odb)
                {
                    _log.LogInfo("item.give: ObjectDB not ready");
                    MaybeToast("StreamBridge: Items not ready");
                    return;
                }

                if (string.IsNullOrEmpty(prefabName))
                {
                    _log.LogInfo("item.give: missing prefab name");
                    MaybeToast("StreamBridge: Missing prefab");
                    return;
                }

                var prefab = odb.GetItemPrefab(prefabName);
                if (!prefab)
                {
                    _log.LogInfo($"item.give: prefab not found '{prefabName}'");
                    MaybeToast($"StreamBridge: Item not found '{prefabName}'");
                    return;
                }

                var itemDrop = prefab.GetComponent<ItemDrop>();
                if (!itemDrop)
                {
                    _log.LogInfo($"item.give: prefab missing ItemDrop '{prefabName}'");
                    MaybeToast($"StreamBridge: Not an item '{prefabName}'");
                    return;
                }

                var shared = itemDrop.m_itemData.m_shared;
                int maxQuality = Mathf.Max(1, shared.m_maxQuality);                  // clamp 1..maxQuality
                int maxStack = Mathf.Max(1, shared.m_maxStackSize);               // clamp to legal stack size

                int wantAmount = Mathf.Max(1, amount);
                int wantQuality = Mathf.Clamp(quality <= 0 ? 1 : quality, 1, maxQuality);
                int wantVariant = Mathf.Max(0, variant);

                int addedStacks = 0;
                int droppedStacks = 0;
                int remaining = wantAmount;

                var inv = player.GetInventory();
                var pos = player.transform.position + player.transform.forward * 1.5f;
                var rot = player.transform.rotation;

                while (remaining > 0)
                {
                    int batch = Mathf.Min(remaining, maxStack);

                    // fresh clone per stack
                    var data = itemDrop.m_itemData.Clone();
                    data.m_stack = batch;
                    data.m_quality = wantQuality;
                    data.m_variant = wantVariant;

                    if (inv.AddItem(data))
                    {
                        addedStacks++;
                        // Optional pickup message (keeps things quiet unless Notify=true)
                        if (BridgePlugin.NotifyEnabled) player.ShowPickupMessage(data, data.m_stack);
                    }
                    else
                    {
                        // Drop gently at feet if inventory can't accept it
                        ItemDrop.DropItem(data, data.m_stack, pos, rot);
                        droppedStacks++;
                    }

                    remaining -= batch;
                }

                _log.LogInfo($"item.give ok: prefab={prefabName} amt={wantAmount} q={wantQuality} v={wantVariant} added={addedStacks} dropped={droppedStacks}");

                if (BridgePlugin.NotifyEnabled)
                {
                    var label = !string.IsNullOrEmpty(shared.m_name) ? shared.m_name : prefabName;
                    MaybeToast($"Gave {wantAmount} × {label}");
                }
            }
            catch (Exception e)
            {
                _log.LogError($"item.give error: {e}");
                MaybeToast("StreamBridge: Item give failed");
            }
        }

        private static void MaybeToast(string msg)
        {
            if (BridgePlugin.NotifyEnabled)
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, msg);
        }
    }
}
