using BepInEx.Logging;
using System;

namespace ValheimBridgeMod
{
    public sealed class ItemService
    {
        private readonly ManualLogSource _log;
        public ItemService(ManualLogSource log) => _log = log;

        public void TryGiveItemGeneric(string prefabName, int amount)
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

                var prefab = odb.GetItemPrefab(prefabName);
                if (prefab == null)
                {
                    _log.LogWarning($"Item prefab not found: {prefabName}");
                    MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"StreamBridge: Item not found '{prefabName}'");
                    return;
                }

                var itemDrop = prefab.GetComponent<ItemDrop>();
                if (itemDrop == null)
                {
                    _log.LogWarning($"ItemDrop missing on prefab: {prefabName}");
                    MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"StreamBridge: ItemDrop missing ({prefabName})");
                    return;
                }

                var data = itemDrop.m_itemData.Clone();
                data.m_stack = Math.Max(1, amount);

                if (player.GetInventory().AddItem(data))
                    player.ShowPickupMessage(data, data.m_stack);
                else
                    ItemDrop.DropItem(data, data.m_stack, player.transform.position + player.transform.forward * 1.5f, player.transform.rotation);

                _log.LogInfo($"Gave {amount}x {prefabName}");
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"StreamBridge: Gave {amount}x {prefabName}");
            }
            catch (Exception e)
            {
                _log.LogError(e);
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"StreamBridge: Give error {e.GetType().Name}");
            }
        }
    }
}
