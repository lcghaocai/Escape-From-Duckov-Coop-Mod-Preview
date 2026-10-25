using Duckov.Quests;
using System.Collections.Generic;
using System.Linq;
using QuestTask = Duckov.Quests.Task;
using Object = UnityEngine.Object;

namespace EscapeFromDuckovCoopMod;

internal static class SharedHomeQuests
{
    private static readonly Dictionary<string, object> Baselines = new();
    private static bool _reloadPending;
    private static float _nextReloadTime;
    private const float ReloadCoalesceSeconds = 0.5f;
    internal static void Init()
    {
        Quest.onQuestStatusChanged += _ => SharedHome.MarkDirty();
        Quest.onQuestActivated += _ => SharedHome.MarkDirty();
        QuestTask.OnTaskStatusChanged += Changed;
    }
    private static string Key(QuestTask task) => task.Master.ID + ":" + task.ID;
    internal static void Changed(QuestTask task)
    {
        if (!task || !task.Master || !SharedHome.Active) return;
        if (!SharedHome.Guest) { SharedHome.MarkDirty(); return; }
        var key = Key(task);
        var value = task.GenerateSaveData();
        if (!SharedHome.Applying && Baselines.TryGetValue(key, out var before) && !Equals(before, value))
        {
            var delta = value is int number && before is int previous ? Math.Max(0, number - previous) : 0;
            SharedHome.Request("task", new { quest = task.Master.ID, task = task.ID, delta, value });
        }
        Baselines[key] = value;
    }
    internal static void Reload()
    {
        // 共享存档里 QuestData 可能因为任务计时/进度变化被频繁下发。
        // 每次全量 Reload 都会销毁并重建所有任务对象，UI 就会反复重排序。
        // 这里做 0.5 秒合并，最多每 0.5 秒重建一次。
        if (UnityEngine.Time.unscaledTime < _nextReloadTime)
        {
            _reloadPending = true;
            return;
        }
        ReloadNow();
    }

    internal static void Tick()
    {
        if (!_reloadPending || UnityEngine.Time.unscaledTime < _nextReloadTime) return;
        _reloadPending = false;
        ReloadNow();
    }

    private static void ReloadNow()
    {
        _nextReloadTime = UnityEngine.Time.unscaledTime + ReloadCoalesceSeconds;

        var manager = QuestManager.Instance;
        if (!manager) return;

        // 记录当前任务顺序；重建后把仍在列表里的任务按原顺序放回去，避免 UI 看起来被重排。
        var activeOrder = manager.ActiveQuests.Where(q => q).Select(q => q.ID).ToList();
        var historyOrder = manager.HistoryQuests.Where(q => q).Select(q => q.ID).ToList();

        foreach (var quest in manager.ActiveQuests.Concat(manager.HistoryQuests).Distinct().ToArray())
            if (quest) { quest.gameObject.SetActive(false); Object.Destroy(quest.gameObject); }
        manager.ActiveQuests.Clear();
        manager.HistoryQuests.Clear();
        Baselines.Clear();
        SharedHomeSave.Load(manager);
        RestoreOrder(manager.ActiveQuests, activeOrder);
        RestoreOrder(manager.HistoryQuests, historyOrder);
        foreach (var quest in manager.ActiveQuests)
            foreach (var task in quest.Tasks)
                if (task) Baselines[Key(task)] = task.GenerateSaveData();
        if (AccessTools.Field(typeof(QuestManager), "onQuestListsChanged")?.GetValue(null) is Action<QuestManager> changed)
            changed(manager);
    }

    private static void RestoreOrder(List<Quest> quests, List<int> order)
    {
        if (quests == null || quests.Count == 0 || order.Count == 0) return;
        var index = new Dictionary<int, int>(order.Count);
        for (var i = 0; i < order.Count; i++) index[order[i]] = i;
        quests.Sort((a, b) =>
        {
            var ai = a && index.TryGetValue(a.ID, out var av) ? av : int.MaxValue;
            var bi = b && index.TryGetValue(b.ID, out var bv) ? bv : int.MaxValue;
            return ai.CompareTo(bi);
        });
    }
}
