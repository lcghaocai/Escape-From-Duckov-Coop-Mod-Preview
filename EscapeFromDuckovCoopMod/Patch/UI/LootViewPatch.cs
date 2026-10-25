// Escape-From-Duckov-Coop-Mod-Preview
// Copyright (C) 2025  Mr.sans and InitLoader's team
//
// This program is not a free software.
// It's distributed under a license based on AGPL-3.0,
// with strict additional restrictions:
//  YOU MUST NOT use this software for commercial purposes.
//  YOU MUST NOT use this software to run a headless game server.
//  YOU MUST include a conspicuous notice of attribution to
//  Mr-sans-and-InitLoader-s-team/Escape-From-Duckov-Coop-Mod-Preview as the original author.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.

using HarmonyLib;
using ItemStatsSystem;

namespace EscapeFromDuckovCoopMod.Patch.UI;

[HarmonyPatch(typeof(Duckov.UI.LootView), "OnStartLoot")]
internal static class LootViewPatch
{
    private static void Postfix(Duckov.UI.LootView __instance, InteractableLootbox lootbox)
    {
        ConfigurePagination(__instance, lootbox != null ? lootbox.Inventory : null);

        var service = NetService.Instance;
        var lootNet = COOPManager.LootNet;

        if (lootbox == null || service == null || service.IsServer || !service.networkStarted || lootNet == null)
            return;

        var inv = lootbox.Inventory;
        if (!LootboxDetectUtil.IsLootboxInventory(inv) || LootboxDetectUtil.IsPrivateInventory(inv))
            return;

        lootNet.Client_SetLootActive(inv, true);
        lootNet.Client_RequestLootState(inv);
    }

    private static void ConfigurePagination(Duckov.UI.LootView view, Inventory inventory)
    {
        if (view == null || inventory == null || inventory.Capacity <= 96) return;

        try
        {
            var display = AccessTools.Field(typeof(Duckov.UI.LootView), "lootTargetInventoryDisplay")
                ?.GetValue(view) as Duckov.UI.InventoryDisplay;
            if (display == null) return;

            var usePages = AccessTools.FieldRefAccess<Duckov.UI.InventoryDisplay, bool>("usePages");
            var itemsEachPage = AccessTools.FieldRefAccess<Duckov.UI.InventoryDisplay, int>("itemsEachPage");
            usePages(display) = true;
            var perPage = itemsEachPage(display);
            if (perPage <= 0 || perPage > 80)
                itemsEachPage(display) = 50;

            // 大仓库只渲染当前页，避免一次创建/布局 700 个槽位。
            display.SetPage(Mathf.Clamp(display.SelectedPage, 0, display.MaxPage));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[LOOT] configure pagination failed: {e.Message}");
        }
    }
}

[HarmonyPatch(typeof(Duckov.UI.LootView), "OnClose")]
internal static class LootViewClosePatch
{
    private static void Postfix(Duckov.UI.LootView __instance)
    {
        var service = NetService.Instance;
        var lootNet = COOPManager.LootNet;

        if (__instance == null || service == null || service.IsServer || lootNet == null)
            return;

        var inv = __instance.TargetInventory;
        if (inv == null) return;

        lootNet.Client_SetLootActive(inv, false);
    }
}
