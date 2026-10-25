using Duckov.Quests;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using QuestTask = Duckov.Quests.Task;
using Object = UnityEngine.Object;

namespace EscapeFromDuckovCoopMod;

internal static class SharedHomeQuests
{
    private static readonly Dictionary<string, object> Baselines = new();
    private static bool _reloadPending;
    private static bool _forceFullQuestData;
    private static float _nextReloadTime;
    private const float ReloadCoalesceSeconds = 0.5f;

    internal static void Init()
    {
        Quest.onQuestStatusChanged += _ =>
        {
            _forceFullQuestData = true;
            SharedHome.MarkDirty();
        };
        Quest.onQuestActivated += _ =>
        {
            _forceFullQuestData = true;
            SharedHome.MarkDirty();
        };
        QuestTask.OnTaskStatusChanged += Changed;
    }
    private static string Key(QuestTask task) => task.Master.ID + ":" + task.ID;
    internal static void Changed(QuestTask task)
    {
        if (!task || !task.Master || !SharedHome.Active) return;
        if (!SharedHome.Guest)
        {
            if (!SharedHome.Applying)
            {
                if (task.IsFinished()) _forceFullQuestData = true;
                else Server_BroadcastTask(task);
            }
            SharedHome.MarkDirty();
            return;
        }
        var key = Key(task);
        var value = task.GenerateSaveData();
        if (!SharedHome.Applying && Baselines.TryGetValue(key, out var before) && !Equals(before, value))
        {
            var delta = value is int number && before is int previous ? Math.Max(0, number - previous) : 0;
            SharedHome.Request("task", new { quest = task.Master.ID, task = task.ID, delta, value });
        }
        Baselines[key] = value;
    }

    /// <summary>
    /// 主机只广播单个任务的进度，不再为每次进度变化重发整份 QuestData。
    /// 这样客机不会销毁/重建任务列表。
    /// </summary>
    internal static void Server_BroadcastTask(QuestTask task)
    {
        if (!task || !NetService.Instance.IsServer || !SharedHome.Active) return;
        var value = task.GenerateSaveData();
        if (value is not int && value is not bool) return;

        var data = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
        {
            quest = task.Master.ID,
            task = task.ID,
            value
        }));
        SharedHome.SendPrivate(12, data);
    }

    /// <summary>
    /// 只有任务激活/完成/状态变化才允许整份 QuestData 下发；
    /// 普通进度/计时变化由 Kind=12 增量处理。
    /// </summary>
    internal static bool ConsumeForceFullQuestData()
    {
        if (!_forceFullQuestData) return false;
        _forceFullQuestData = false;
        return true;
    }

    internal static void ReceiveTaskDelta(byte[] data)
    {
        if (data == null || data.Length == 0 || !SharedHome.Guest) return;
        try
        {
            var args = JObject.Parse(Encoding.UTF8.GetString(data));
            var questId = (int)args["quest"];
            var taskId = (int)args["task"];
            var manager = QuestManager.Instance;
            if (!manager) return;

            var quest = manager.ActiveQuests.Concat(manager.HistoryQuests)
                .FirstOrDefault(q => q && q.ID == questId);
            var task = quest?.Tasks.FirstOrDefault(t => t && t.ID == taskId);
            if (!task) return;

            var token = args["value"];
            SharedHome.Apply(() =>
            {
                if (token != null && token.Type == JTokenType.Integer)
                {
                    task.SetupSaveData((int)token);
                }
                else if (token != null && token.Type == JTokenType.Boolean)
                {
                    var flag = (bool)token;
                    if (task is Duckov.Quests.Tasks.QuestTask_CheckSaveData check)
                        SharedHomeWorld.SetFlag(check.SaveDataKey, flag);
                    else
                        task.SetupSaveData(flag);
                }
                else
                {
                    return;
                }

                AccessTools.Method(typeof(QuestTask), "ReportStatusChanged")?.Invoke(task, null);
            });
        }
        catch (System.Exception e)
        {
            CoopLogSystem.WriteNetworkDiagnostic($"[SharedHome] quest task delta failed: {e}");
        }
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
