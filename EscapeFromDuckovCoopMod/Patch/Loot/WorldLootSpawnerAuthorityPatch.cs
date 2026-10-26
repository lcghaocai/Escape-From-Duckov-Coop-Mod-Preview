using Duckov.Utilities;
using ItemStatsSystem;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace EscapeFromDuckovCoopMod.Patch.Loot;

// Setup awaits item assets. Intercept its actual drop call rather than keeping
// a thread-local "spawning" flag across awaits (which can affect player drops).
[HarmonyPatch]
internal static class WorldLootSpawnerAuthorityPatch
{
    private static int _suppressed;
    private static float _nextLog;
    private static readonly MethodInfo NativeDrop = AccessTools.Method(typeof(ItemExtensions), nameof(ItemExtensions.Drop),
        new[] { typeof(Item), typeof(Vector3), typeof(bool), typeof(Vector3), typeof(float) });

    private static MethodBase TargetMethod() => AccessTools.Method(typeof(LootSpawner), nameof(LootSpawner.Setup))
        .GetCustomAttribute<AsyncStateMachineAttribute>().StateMachineType.GetMethod("MoveNext",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var replacement = AccessTools.Method(typeof(WorldLootSpawnerAuthorityPatch), nameof(DropGeneratedLoot));
        var replaced = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(NativeDrop))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
                replaced++;
            }
            yield return instruction;
        }
        if (replaced != 1)
            Debug.LogError($"[LOOT] Unexpected LootSpawner drop call count: {replaced}; check game compatibility.");
    }

    private static DuckovItemAgent DropGeneratedLoot(Item item, Vector3 position, bool rigidbody, Vector3 direction, float angle)
    {
        var service = NetService.Instance;
        if (service == null || !service.networkStarted || service.IsServer)
            return item.Drop(position, rigidbody, direction, angle);
        if (!item) return null;

        if (ItemTool.HasQuestTag(item))
        {
            // Quest loot is intentionally personal and excluded from host snapshots.
            // Bypass the ordinary client drop-request hook for this native spawn.
            var wasNetworkDrop = ItemNet.InNetworkDrop;
            try
            {
                ItemNet.InNetworkDrop = true;
                return item.Drop(position, rigidbody, direction, angle);
            }
            finally { ItemNet.InNetworkDrop = wasNetworkDrop; }
        }

        // This is a newly generated local candidate, never a player's inventory
        // item. Do not expose an unnumbered pickup or forward it as a player drop.
        // Setup writes FromInfoKey after Drop returns, so release on the next frame.
        ReleaseGeneratedCandidate(item).Forget();
        _suppressed++;
        if (Time.unscaledTime >= _nextLog)
        {
            _nextLog = Time.unscaledTime + 5f;
            CoopLogSystem.WriteNetworkDiagnostic($"[LOOT][WorldSpawn] suppressedLocal={_suppressed}; ordinary world loot uses host drops");
        }
        return null;
    }

    private static async UniTaskVoid ReleaseGeneratedCandidate(Item item)
    {
        // This also works when the mod assembly was loaded after Unity's runtime
        // initialization callbacks; it does not depend on a separate runner.
        await UniTask.NextFrame();
        if (item) item.DestroyTree();
    }
}
