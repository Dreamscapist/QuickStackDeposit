using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace QuickStackDeposit
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGUID = "dreamscapist.valheim.quickstackdeposit";
        public const string PluginName = "QuickStackDeposit";
        public const string PluginVersion = "1.0.2";

        // ---- Config ----
        private static ConfigEntry<KeyboardShortcut> _depositKey;
        private static ConfigEntry<float> _depositRange;
        private static ConfigEntry<bool> _includeEquipped;

        // Cached scan results, refreshed only when the inventory screen transitions closed -> open.
        private static bool _wasInventoryOpen;
        private static List<Container> _cachedNearbyContainers = new List<Container>();

        // Valheim's hotbar (slots reachable with number keys 1-8) is just the
        // top row of the inventory grid, y == 0. Anything in that row is left alone.
        private const int HotbarRow = 0;

        private void Awake()
        {
            _depositKey = Config.Bind(
                "General",
                "DepositKey",
                new KeyboardShortcut(KeyCode.R),
                "Key to press while your inventory screen is open to deposit matching items into nearby storages."
            );

            _depositRange = Config.Bind(
                "General",
                "DepositRange",
                20f,
                new ConfigDescription(
                    "How far away (in meters) to look for storages to deposit into.",
                    new AcceptableValueRange<float>(10f, 100f)
                )
            );

            _includeEquipped = Config.Bind(
                "General",
                "IncludeEquippedItems",
                false,
                "If true, currently equipped items (weapons, armor, etc.) can also be deposited. Off by default so you don't accidentally strip your gear."
            );

            Logger.LogInfo($"{PluginName} {PluginVersion} loaded. Deposit key: {_depositKey.Value}, range: {_depositRange.Value}");
        }

        private void Update()
        {
            if (Player.m_localPlayer == null) return;

            bool isInventoryOpen = InventoryGui.instance != null && InventoryGui.IsVisible();

            // Inventory just opened this frame -> scan for nearby storages once and cache them.
            if (isInventoryOpen && !_wasInventoryOpen)
            {
                _cachedNearbyContainers = FindNearbyContainers(Player.m_localPlayer, _depositRange.Value);
            }

            _wasInventoryOpen = isInventoryOpen;

            if (!isInventoryOpen) return;
            if (Chat.instance != null && Chat.instance.HasFocus()) return;
            if (Console.IsVisible()) return;

            if (_depositKey.Value.IsDown())
            {
                DepositNearbyMatchingItems(Player.m_localPlayer, _cachedNearbyContainers);
            }
        }

        private void DepositNearbyMatchingItems(Player player, List<Container> containers)
        {
            // Drop any containers that were destroyed/moved out of range since the scan.
            containers.RemoveAll(c => c == null);

            if (containers.Count == 0)
            {
                player.Message(MessageHud.MessageType.Center, "No nearby storages in range");
                return;
            }

            Inventory playerInventory = player.GetInventory();
            // Snapshot the list since we'll be mutating the player's inventory while iterating.
            List<ItemDrop.ItemData> playerItems = new List<ItemDrop.ItemData>(playerInventory.GetAllItems());

            int stacksMoved = 0;

            foreach (ItemDrop.ItemData item in playerItems)
            {
                if (item == null) continue;
                if (item.m_equipped && !_includeEquipped.Value) continue;
                if (item.m_gridPos.y == HotbarRow) continue;

                foreach (Container container in containers)
                {
                    if (container == null) continue;

                    Inventory containerInventory = container.GetInventory();
                    if (containerInventory == null) continue;

                    // Only deposit into a container that already stocks this exact item type.
                    if (!containerInventory.HaveItem(item.m_shared.m_name)) continue;

                    // MoveItemToThis merges into existing stacks first, then free slots,
                    // and safely handles the case where only part of the stack fits.
                    // (It returns void in this game version, so we detect success by
                    // comparing the stack size / presence before and after the call.)
                    int stackBefore = item.m_stack;
                    containerInventory.MoveItemToThis(playerInventory, item);
                    bool stillInPlayerInventory = playerInventory.ContainsItem(item);
                    bool moved = !stillInPlayerInventory || item.m_stack < stackBefore;

                    if (moved)
                    {
                        stacksMoved++;
                    }

                    // If the whole stack is already gone from the player, no need to check other containers for it.
                    if (!playerInventory.ContainsItem(item))
                    {
                        break;
                    }
                }
            }

            // No manual save needed: Container listens for inventory changes
            // internally and persists itself automatically.

            if (stacksMoved > 0)
            {
                player.Message(MessageHud.MessageType.Center, "Deposited items into nearby storage(s)");
            }
            else
            {
                player.Message(MessageHud.MessageType.Center, "Nothing to deposit");
            }
        }

        private List<Container> FindNearbyContainers(Player player, float range)
        {
            List<Container> result = new List<Container>();
            // No layer mask here on purpose: chests, carts, boats, and other
            // storage-like pieces don't all sit on the same physics layer, so we
            // scan everything nearby and filter down to actual Container components.
            Collider[] hits = Physics.OverlapSphere(player.transform.position, range);

            foreach (Collider hit in hits)
            {
                Container container = hit.GetComponentInParent<Container>();
                if (container == null) continue;
                if (result.Contains(container)) continue;

                // Skip containers currently opened by someone else.
                // Note: this doesn't check ward/private-area permissions - direct
                // inventory access like this bypasses the interact-based ward check,
                // same as most other "quick stack" mods. Keep that in mind on servers.
                if (container.IsInUse()) continue;

                result.Add(container);
            }

            return result;
        }
    }
}
