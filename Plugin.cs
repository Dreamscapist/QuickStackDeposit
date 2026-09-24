using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace QuickStackDeposit
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGUID = "dreamscapist.valheim.quickstackdeposit";
        public const string PluginName = "QuickStackDeposit";
        public const string PluginVersion = "1.1.2";

        // ---- Config ----
        private static ConfigEntry<KeyboardShortcut> _depositKey;
        private static ConfigEntry<KeyboardShortcut> _lockToggleKey;
        private static ConfigEntry<float> _depositRange;
        private static ConfigEntry<bool> _includeEquipped;

        // Valheim's hotbar (slots reachable with number keys 1-8) is just the
        // top row of the inventory grid, y == 0. Anything in that row is left alone.
        private const int HotbarRow = 0;

        // Locked slots are tracked by grid position, not by item, so a slot stays
        // locked even if you empty it and put something else there. Persisted to
        // disk per-character (keyed by Player.GetPlayerID()) so locks survive
        // restarts. File lives next to the plugin's own config file.
        private static readonly Dictionary<long, HashSet<Vector2i>> _allPlayerLocks = new Dictionary<long, HashSet<Vector2i>>();
        private static string LocksFilePath => Path.Combine(Paths.ConfigPath, PluginGUID + ".locks.txt");

        private static ManualLogSource _log;

        private static ConfigEntry<bool> _clearLocksEntry;
        private static ConfigEntry<bool> _debugLogging;

        // Tracks the inventory open/closed transition so lock-border UI elements
        // are (re)built exactly once per open, and cleaned up once per close.
        private static bool _wasInventoryOpenForOverlay;

        private void Awake()
        {
            _log = Logger;

            LoadLocksFromDisk();

            _depositKey = Config.Bind(
                "General",
                "DepositKey",
                new KeyboardShortcut(KeyCode.R),
                "Key to press while your inventory screen is open to deposit matching items into nearby storages."
            );

            _lockToggleKey = Config.Bind(
                "General",
                "LockToggleKey",
                new KeyboardShortcut(KeyCode.X),
                "Key to press while hovering over an inventory slot to lock/unlock it. Locked slots are never deposited."
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

            _debugLogging = Config.Bind(
                "Debug",
                "EnableDebugLogging",
                false,
                "If true, logs detailed diagnostic messages (slot resolution, deposit decisions) to the BepInEx console/log. Off by default to keep the console clean."
            );

            // Adds a real button in BepInEx Configuration Manager (if installed) via
            // its "custom drawer" convention - this only needs a locally-defined
            // class with a matching name/field, not a compile-time reference to the
            // Configuration Manager assembly. The underlying bool value is unused;
            // it's just a hook for the button widget. Without Configuration Manager
            // installed this entry simply sits unused in the .cfg file.
            _clearLocksEntry = Config.Bind(
                "Locking",
                "ClearAllLocks",
                false,
                new ConfigDescription(
                    "Button (in Configuration Manager): unlocks every locked slot for your current character.",
                    null,
                    new ConfigurationManagerAttributes { CustomDrawer = ClearLocksButtonDrawer }
                )
            );

            Logger.LogInfo($"{PluginName} {PluginVersion} loaded. Deposit key: {_depositKey.Value}, lock key: {_lockToggleKey.Value}, range: {_depositRange.Value}");
        }

        // Duck-typed on purpose: BepInEx.ConfigurationManager looks for tag objects
        // by type NAME, not by assembly identity, so this local class works without
        // referencing that mod's assembly at all (and degrades harmlessly if the
        // player doesn't have Configuration Manager installed).
        private class ConfigurationManagerAttributes
        {
            public Action<ConfigEntryBase> CustomDrawer;
        }

        private static void ClearLocksButtonDrawer(ConfigEntryBase entry)
        {
            if (GUILayout.Button("Clear all locked slots for current character"))
            {
                ClearLocksForCurrentPlayer();
            }
        }

        private static void ClearLocksForCurrentPlayer()
        {
            if (Player.m_localPlayer == null)
            {
                DebugLog("[Locks] Clear requested but no character is loaded right now.");
                return;
            }

            HashSet<Vector2i> set = GetLockedSlotsForPlayer(Player.m_localPlayer);
            int count = set.Count;
            set.Clear();
            SaveLocksToDisk();
            RebuildLockBorders(Player.m_localPlayer);

            DebugLog($"[Locks] Cleared {count} locked slot(s) for current character.");
            Player.m_localPlayer.Message(MessageHud.MessageType.Center, $"Cleared {count} locked slot(s)");
        }

        private static HashSet<Vector2i> GetLockedSlotsForPlayer(Player player)
        {
            long id = player.GetPlayerID();
            if (!_allPlayerLocks.TryGetValue(id, out HashSet<Vector2i> set))
            {
                set = new HashSet<Vector2i>();
                _allPlayerLocks[id] = set;
            }
            return set;
        }

        private static void LoadLocksFromDisk()
        {
            _allPlayerLocks.Clear();
            try
            {
                if (!File.Exists(LocksFilePath)) return;

                foreach (string line in File.ReadAllLines(LocksFilePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    string[] parts = line.Split('|');
                    if (parts.Length != 2) continue;
                    if (!long.TryParse(parts[0], out long playerId)) continue;

                    HashSet<Vector2i> set = new HashSet<Vector2i>();
                    foreach (string posStr in parts[1].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string[] xy = posStr.Split(',');
                        if (xy.Length == 2 && int.TryParse(xy[0], out int x) && int.TryParse(xy[1], out int y))
                        {
                            set.Add(new Vector2i(x, y));
                        }
                    }
                    _allPlayerLocks[playerId] = set;
                }

                DebugLog($"[Locks] Loaded locks for {_allPlayerLocks.Count} character(s) from disk.");
            }
            catch (Exception e)
            {
                _log?.LogWarning($"[Locks] Failed to load locks file: {e}");
            }
        }

        private static void SaveLocksToDisk()
        {
            try
            {
                List<string> lines = new List<string>();
                foreach (KeyValuePair<long, HashSet<Vector2i>> kvp in _allPlayerLocks)
                {
                    if (kvp.Value.Count == 0) continue;
                    string posList = string.Join(";", kvp.Value.Select(p => $"{p.x},{p.y}"));
                    lines.Add($"{kvp.Key}|{posList}");
                }
                File.WriteAllLines(LocksFilePath, lines);
            }
            catch (Exception e)
            {
                _log?.LogWarning($"[Locks] Failed to save locks file: {e}");
            }
        }

        private void Update()
        {
            if (Player.m_localPlayer == null)
            {
                if (_wasInventoryOpenForOverlay)
                {
                    ClearLockBorders();
                    _wasInventoryOpenForOverlay = false;
                    _pendingBorderRebuild = false;
                }
                return;
            }

            bool isInventoryOpen = InventoryGui.instance != null && InventoryGui.IsVisible();

            if (isInventoryOpen && !_wasInventoryOpenForOverlay)
            {
                // Don't rebuild right here: our Update() can run before Valheim's own
                // script populates the grid's slot GameObjects for this frame. Defer
                // to LateUpdate, which always runs after every other script's Update
                // in the same frame.
                _pendingBorderRebuild = true;
            }
            else if (!isInventoryOpen && _wasInventoryOpenForOverlay)
            {
                ClearLockBorders();
                _pendingBorderRebuild = false;
            }
            _wasInventoryOpenForOverlay = isInventoryOpen;

            if (!isInventoryOpen) return;
            if (Chat.instance != null && Chat.instance.HasFocus()) return;
            if (Console.IsVisible()) return;

            if (_lockToggleKey.Value.IsDown())
            {
                ToggleLockOnHoveredSlot(Player.m_localPlayer);
            }

            if (_depositKey.Value.IsDown())
            {
                List<Container> containers = FindNearbyContainers(Player.m_localPlayer, _depositRange.Value);
                DepositNearbyMatchingItems(Player.m_localPlayer, containers);
            }
        }

        private void LateUpdate()
        {
            if (Player.m_localPlayer == null) return;

            if (_pendingBorderRebuild)
            {
                RebuildLockBorders(Player.m_localPlayer);
                _pendingBorderRebuild = false;

                // Safety net: if the grid's slot elements genuinely weren't populated
                // yet even by LateUpdate (e.g. deferred a full frame), and we had
                // locks to show but found nothing to attach them to, try once more
                // shortly after instead of silently giving up for the whole session.
                if (_activeLockBorders.Count == 0 && GetLockedSlotsForPlayer(Player.m_localPlayer).Count > 0)
                {
                    _borderRetryAt = Time.unscaledTime + 0.3f;
                    _borderRetryPending = true;
                }
            }
            else if (_borderRetryPending && Time.unscaledTime >= _borderRetryAt)
            {
                _borderRetryPending = false;
                if (InventoryGui.instance != null && InventoryGui.IsVisible())
                {
                    RebuildLockBorders(Player.m_localPlayer);
                }
            }
        }

        // Instead of drawing an OnGUI overlay (which always renders on top of every
        // Canvas-based window, including popups like the split-stack dialog opened
        // later), this parents real UI elements directly onto each locked slot's own
        // RectTransform. That makes them ordinary children of the game's own UI
        // hierarchy, so anything opened afterward covers them normally instead of
        // the border punching through on top of it.
        private static readonly List<GameObject> _activeLockBorders = new List<GameObject>();
        private static Type _uiImageType;
        private static Type UIImageType => _uiImageType ?? (_uiImageType = FindRuntimeType("UnityEngine.UI.Image"));
        private static bool _pendingBorderRebuild;
        private static bool _borderRetryPending;
        private static float _borderRetryAt;

        private static void ClearLockBorders()
        {
            foreach (GameObject go in _activeLockBorders)
            {
                if (go != null) UnityEngine.Object.Destroy(go);
            }
            _activeLockBorders.Clear();
        }

        private static void RebuildLockBorders(Player player)
        {
            ClearLockBorders();

            if (UIImageType == null)
            {
                DebugLog("[Overlay] UnityEngine.UI.Image type not found at runtime; cannot draw slot borders.");
                return;
            }

            HashSet<Vector2i> lockedSlots = GetLockedSlotsForPlayer(player);
            if (lockedSlots.Count == 0) return;

            List<(Vector2i pos, RectTransform rect)> slots = FindAllSlotRects();
            foreach ((Vector2i pos, RectTransform rect) in slots)
            {
                if (rect == null || !lockedSlots.Contains(pos)) continue;
                CreateBorderOn(rect);
            }

            DebugLog($"[Overlay] Built borders for {lockedSlots.Count} locked slot(s) ({_activeLockBorders.Count} bar objects).");
        }

        private const float BorderThickness = 3f;

        private static void CreateBorderOn(RectTransform parent)
        {
            Color color = Color.gray;
            CreateBorderBar(parent, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), new Vector2(0, BorderThickness), color); // top
            CreateBorderBar(parent, new Vector2(0, 0), new Vector2(1, 0), new Vector2(0.5f, 0), new Vector2(0, BorderThickness), color); // bottom
            CreateBorderBar(parent, new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0.5f), new Vector2(BorderThickness, 0), color); // left
            CreateBorderBar(parent, new Vector2(1, 0), new Vector2(1, 1), new Vector2(1, 0.5f), new Vector2(BorderThickness, 0), color); // right
        }

        private static void CreateBorderBar(RectTransform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 sizeDelta, Color color)
        {
            GameObject go = new GameObject("QSD_LockBorder", typeof(RectTransform), UIImageType);
            go.transform.SetParent(parent, false);

            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot;
            rt.sizeDelta = sizeDelta;
            rt.anchoredPosition = Vector2.zero;

            Component image = go.GetComponent(UIImageType);
            UIImageType.GetProperty("color")?.SetValue(image, color);
            UIImageType.GetProperty("raycastTarget")?.SetValue(image, false);

            _activeLockBorders.Add(go);
        }

        // Walks the player's inventory grid hierarchy looking for the same kind of
        // per-slot Vector2i field used for hover/lock resolution, but for every slot
        // at once instead of just the one under the mouse.
        private static List<(Vector2i pos, RectTransform rect)> FindAllSlotRects()
        {
            List<(Vector2i, RectTransform)> results = new List<(Vector2i, RectTransform)>();

            InventoryGrid grid = InventoryGui.instance != null ? InventoryGui.instance.m_playerGrid : null;
            if (grid == null) return results;

            HashSet<Vector2i> seen = new HashSet<Vector2i>();
            CollectSlotRectsRecursive(grid.transform, results, seen, 0);

            DebugLog($"[Overlay] Found {results.Count} slot rects under player grid.");
            return results;
        }

        private static void CollectSlotRectsRecursive(Transform t, List<(Vector2i, RectTransform)> results, HashSet<Vector2i> seen, int depth)
        {
            if (t == null || depth > 10) return;

            foreach (Component comp in t.GetComponents<Component>())
            {
                if (comp == null) continue;

                Vector2i? pos = FindVector2iField(comp);
                if (pos.HasValue && seen.Add(pos.Value) && t is RectTransform rt)
                {
                    results.Add((pos.Value, rt));
                }
            }

            for (int i = 0; i < t.childCount; i++)
            {
                CollectSlotRectsRecursive(t.GetChild(i), results, seen, depth + 1);
            }
        }

        private void ToggleLockOnHoveredSlot(Player player)
        {
            Vector2i? pos = FindHoveredSlotPos(player);
            if (pos == null)
            {
                DebugLog($"[LockToggle] No slot resolved under mouse at {Input.mousePosition}.");
                return;
            }

            HashSet<Vector2i> lockedSlots = GetLockedSlotsForPlayer(player);

            bool nowLocked;
            if (lockedSlots.Contains(pos.Value))
            {
                lockedSlots.Remove(pos.Value);
                nowLocked = false;
            }
            else
            {
                lockedSlots.Add(pos.Value);
                nowLocked = true;
            }

            SaveLocksToDisk();
            RebuildLockBorders(player);

            DebugLog($"[LockToggle] {(nowLocked ? "Locking" : "Unlocking")} slot {pos.Value}. Currently locked slots: {string.Join(", ", lockedSlots)}");

            player.Message(MessageHud.MessageType.Center, nowLocked ? "Inventory slot locked" : "Inventory slot unlocked");
        }

        // Rather than guess at InventoryGrid's internal layout (which produced
        // unreliable results - see version history), this uses Unity's own UI
        // event system to raycast at the mouse position, exactly like the game
        // itself does to know what's under the cursor. It's resolved entirely via
        // reflection so it doesn't need a compile-time reference to UnityEngine.UI -
        // the types are already loaded at runtime since the game's whole UI depends
        // on them. From the hit GameObject, it walks up the parent chain looking for
        // any component field that directly holds a Vector2i grid position (works
        // for empty slots too), falling back to an ItemData field's own position.
        private static Vector2i? FindHoveredSlotPos(Player player)
        {
            GameObject hit = RaycastTopmostUIElement(Input.mousePosition);
            if (hit == null)
            {
                DebugLog("[LockToggle] UI raycast found nothing under the mouse.");
                return null;
            }

            DebugLog($"[LockToggle] UI raycast hit: '{GetHierarchyPath(hit.transform)}'");

            Transform t = hit.transform;
            int depth = 0;
            while (t != null && depth < 12)
            {
                foreach (Component comp in t.GetComponents<Component>())
                {
                    if (comp == null) continue;

                    Vector2i? pos = FindVector2iField(comp);
                    if (pos.HasValue)
                    {
                        DebugLog($"[LockToggle] Found Vector2i {pos.Value} on {comp.GetType().Name} at '{t.name}' (depth {depth}).");
                        return pos;
                    }

                    ItemDrop.ItemData directItem = FindItemDataField(comp);
                    if (directItem != null)
                    {
                        DebugLog($"[LockToggle] Found ItemData field on {comp.GetType().Name} at '{t.name}' (depth {depth}): '{directItem.m_shared.m_name}' at {directItem.m_gridPos}.");
                        return directItem.m_gridPos;
                    }
                }

                t = t.parent;
                depth++;
            }

            DebugLog("[LockToggle] Walked up the hit hierarchy but found no ItemData/Vector2i field on any component.");
            return null;
        }

        private static GameObject RaycastTopmostUIElement(Vector2 screenPos)
        {
            Type eventSystemType = FindRuntimeType("UnityEngine.EventSystems.EventSystem");
            Type pointerEventDataType = FindRuntimeType("UnityEngine.EventSystems.PointerEventData");
            Type raycastResultType = FindRuntimeType("UnityEngine.EventSystems.RaycastResult");
            if (eventSystemType == null || pointerEventDataType == null || raycastResultType == null)
            {
                DebugLog("[LockToggle] Could not find UnityEngine.EventSystems types at runtime.");
                return null;
            }

            object current = eventSystemType.GetProperty("current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (current == null)
            {
                DebugLog("[LockToggle] EventSystem.current is null.");
                return null;
            }

            object pointerData = Activator.CreateInstance(pointerEventDataType, current);
            pointerEventDataType.GetProperty("position")?.SetValue(pointerData, screenPos);

            Type listType = typeof(List<>).MakeGenericType(raycastResultType);
            object resultsList = Activator.CreateInstance(listType);

            MethodInfo raycastAllMethod = eventSystemType.GetMethod("RaycastAll", new[] { pointerEventDataType, listType });
            if (raycastAllMethod == null)
            {
                DebugLog("[LockToggle] EventSystem.RaycastAll method not found.");
                return null;
            }

            raycastAllMethod.Invoke(current, new object[] { pointerData, resultsList });

            IList results = (IList)resultsList;
            if (results.Count == 0) return null;

            PropertyInfo goProp = raycastResultType.GetProperty("gameObject");
            return goProp?.GetValue(results[0]) as GameObject;
        }

        private static Type FindRuntimeType(string fullName)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = SafeGet(() => asm.GetType(fullName)) as Type;
                if (t != null) return t;
            }
            return null;
        }

        private static string GetHierarchyPath(Transform t)
        {
            List<string> names = new List<string>();
            while (t != null)
            {
                names.Add(t.name);
                t = t.parent;
            }
            names.Reverse();
            return string.Join("/", names);
        }


        private static ItemDrop.ItemData FindItemDataField(object element)
        {
            Type type = element.GetType();
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field.FieldType == typeof(ItemDrop.ItemData))
                {
                    return SafeGet(() => field.GetValue(element)) as ItemDrop.ItemData;
                }
            }
            return null;
        }

        private static Vector2i? FindVector2iField(object element)
        {
            Type type = element.GetType();
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field.FieldType == typeof(Vector2i))
                {
                    object value = SafeGet(() => field.GetValue(element));
                    if (value != null) return (Vector2i)value;
                }
            }
            return null;
        }

        private static object SafeGet(Func<object> getter)
        {
            try { return getter(); }
            catch { return null; }
        }

        // Gated behind EnableDebugLogging (off by default) so the console/log
        // stays quiet during normal play; flip it on in Configuration Manager
        // (or the .cfg file) when diagnosing slot-resolution issues.
        private static void DebugLog(string message)
        {
            if (_debugLogging != null && _debugLogging.Value)
            {
                _log.LogInfo(message);
            }
        }

        private static bool IsSlotLocked(Player player, Vector2i pos)
        {
            return GetLockedSlotsForPlayer(player).Contains(pos);
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
                if (IsSlotLocked(player, item.m_gridPos))
                {
                    DebugLog($"[Deposit] Skipping item '{item.m_shared.m_name}' - slot {item.m_gridPos} is locked.");
                    continue;
                }

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
                        DebugLog($"[Deposit] Moved '{item.m_shared.m_name}' (was at {item.m_gridPos}) into a nearby storage.");
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
