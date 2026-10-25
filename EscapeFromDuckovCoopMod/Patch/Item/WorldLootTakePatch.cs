using System.Reflection;
using ItemStatsSystem;
using ItemStatsSystem.Items;

namespace EscapeFromDuckovCoopMod;

// Stop the transfer at its public entry point. Blocking RemoveAt alone is not
// enough: Item.Detach returns void and its caller can still insert the preview.
[HarmonyPatch]
internal static class WorldLootNativeTakePatch
{
    private static IEnumerable<MethodBase> TargetMethods() => typeof(ItemUtilities).GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Where(m => m.Name == "SendToPlayerCharacter" || m.Name == "SendToPlayerCharacterInventory");
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(Item item, ref bool __result)
    {
        if (COOPManager.LootNet?.RequestTake(item) != true) return true;
        __result = true;
        return false;
    }
}
[HarmonyPatch(typeof(Inventory), nameof(Inventory.AddAt))]
internal static class WorldLootTakeAddAtPatch
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(Inventory __instance, Item item, int atPosition, ref bool __result)
    {
        if (COOPManager.LootNet?.RequestTake(item, __instance, atPosition) != true) return true;
        __result = true;
        return false;
    }
}
[HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItem))]
internal static class WorldLootTakeAddItemPatch
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(Inventory __instance, Item item, ref bool __result)
    {
        if (COOPManager.LootNet?.RequestTake(item, __instance) != true) return true;
        __result = true;
        return false;
    }
}
[HarmonyPatch(typeof(ItemUtilities), nameof(ItemUtilities.AddAndMerge))]
internal static class WorldLootTakeMergeAddPatch
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(Inventory inventory, Item item, ref bool __result)
    {
        if (COOPManager.LootNet?.RequestTake(item, inventory) != true) return true;
        __result = true;
        return false;
    }
}
[HarmonyPatch(typeof(Slot), nameof(Slot.Plug))]
internal static class WorldLootTakeEquipPatch
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(Slot __instance, Item otherItem, object[] __args, ref bool __result)
    {
        if (COOPManager.LootNet?.RequestTake(otherItem, slot: __instance) != true) return true;
        __args[1] = null; __result = true;
        return false;
    }
}
[HarmonyPatch(typeof(Item), nameof(Item.Detach))]
internal static class WorldLootHoldPatch
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(Item __instance) => COOPManager.LootNet?.HoldWorldLoot(__instance.InInventory) != true;
}
[HarmonyPatch(typeof(Item), nameof(Item.Split))]
internal static class WorldLootTakeSplitPatch
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(Item __instance, int count, ref UniTask<Item> __result)
    {
        var net = COOPManager.LootNet;
        if (net?.HoldWorldLoot(__instance.InInventory) != true) return true;
        __result = net.SplitWorldLoot(__instance, count);
        return false;
    }
}
[HarmonyPatch(typeof(Item), nameof(Item.Combine))]
internal static class WorldLootTakeCombinePatch
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(Item __instance, Item incomingItem)
    {
        var net = COOPManager.LootNet;
        if (!__instance || !incomingItem || net == null) return true;
        if (!net.HoldWorldLoot(incomingItem.InInventory)) return true;
        var count = Math.Min(incomingItem.StackCount, __instance.MaxStackCount - __instance.StackCount);
        if (__instance.Stackable && __instance.TypeID == incomingItem.TypeID && count > 0 && !net.HoldWorldLoot(__instance.InInventory))
            net.RequestTake(incomingItem, merge: __instance, count: count);
        return false;
    }
}
[HarmonyPatch(typeof(CharacterMainControl), nameof(CharacterMainControl.UseItem))]
internal static class WorldLootTakeUsePatch
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(CharacterMainControl __instance, Item item) => __instance != CharacterMainControl.Main ||
        !item || !item.IsUsable(__instance) || COOPManager.LootNet?.RequestTake(item, count: 1, use: true) != true;
}
