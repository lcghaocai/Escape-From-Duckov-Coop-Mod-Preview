namespace EscapeFromDuckovCoopMod.Patch.Loot;

[HarmonyPatch(typeof(CA_Carry), "OnStart")]
internal static class CorpseCarryStartPatch
{
    private static bool Prefix(CA_Carry __instance, ref bool __result)
    {
        var sync = CorpseCarrySync.For(__instance.carryTarget);
        if (!sync || sync.BeginLocal(__instance)) return true;
        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(Carriable), nameof(Carriable.Drop))]
internal static class CorpseCarryDropPatch
{
    private static void Postfix(Carriable __instance) => CorpseCarrySync.For(__instance)?.DropLocal();
}
