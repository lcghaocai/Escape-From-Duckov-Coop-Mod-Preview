using System.Reflection;
using Duckov.UI;

namespace EscapeFromDuckovCoopMod;

// Optional bridge: do not load or require the camera mod when it is absent.
internal static class ShoulderSurfingCompat
{
    private static FieldInfo _instance, _target, _initialized, _lastHurt;
    private static float _nextResolve;
    private static bool _warned;
    private static readonly MethodInfo GetHealthBar = AccessTools.DeclaredMethod(
        typeof(HealthBarManager), "GetActiveHealthBar", new[] { typeof(Health) });

    private static bool Resolve()
    {
        if (_initialized != null) return true;
        if (Time.unscaledTime < _nextResolve) return false;
        _nextResolve = Time.unscaledTime + 5f;
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "ShoulderSurfing");
        var camera = assembly?.GetType("ShoulderSurfing.ShoulderCamera");
        if (camera == null) return false;
        _instance = AccessTools.Field(camera, "__Instance");
        _target = AccessTools.Field(camera, "target");
        var common = assembly.GetType("HealthBarCommon");
        _lastHurt = common == null ? null : AccessTools.Field(common, "LastTimeToHurt");
        _initialized = AccessTools.Field(camera, "shoulderCameraInitalized");
        return _initialized != null;
    }

    internal static void SetSpectatorTarget(CharacterMainControl target)
    {
        if (!target) return;
        try
        {
            if (!Resolve() || _initialized.GetValue(null) is not true || _target == null) return;
            var camera = _instance?.GetValue(null) as MonoBehaviour;
            if (camera && !ReferenceEquals(_target.GetValue(camera), target))
                _target.SetValue(camera, target);
            // Never change LevelManager's controlling character: that would give
            // local movement/fire input to the remote player being spectated.
        }
        catch (Exception ex) { Warn(ex); }
    }

    internal static void NotifyReplicatedHurt(Health health)
    {
        try
        {
            if (!health || !health.showHealthBar || !Resolve() || _initialized.GetValue(null) is not true) return;
            if (_lastHurt?.GetValue(null) is not Dictionary<HealthBar, float> lastHurt) return;
            var manager = HealthBarManager.Instance;
            if (!manager || GetHealthBar == null) return;
            health.RequestHealthBar();
            var bar = GetHealthBar.Invoke(manager, new object[] { health }) as HealthBar;
            if (bar) lastHurt[bar] = Time.time;
            // Only refresh the camera mod's three-second UI timer. Do not replay
            // Health.OnHurtEvent, which also drives gameplay and damage reports.
        }
        catch (Exception ex) { Warn(ex); }
    }

    private static void Warn(Exception ex)
    {
        if (_warned) return;
        _warned = true;
        CoopLogSystem.WriteNetworkDiagnostic($"[TPS] Optional ShoulderSurfing bridge failed: {ex.Message}");
    }
}
