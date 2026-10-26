namespace EscapeFromDuckovCoopMod;

[DisallowMultipleComponent]
internal sealed class RemoteHealthState : MonoBehaviour
{
    public bool HasSnapshot { get; private set; }
    public float MaxHealth { get; private set; }
    public float CurrentHealth { get; private set; }

    public static RemoteHealthState Get(Health health) => health ? health.GetComponent<RemoteHealthState>() : null;

    public static bool TryApply(Health health, float max, float current)
    {
        var character = health.TryGetCharacter();
        if (!character || character.IsMainCharacter ||
            (!character.GetComponent<RemoteReplicaTag>() && !character.GetComponent<RemoteAIReplicaTag>()))
            return false;

        var state = Get(health) ?? health.gameObject.AddComponent<RemoteHealthState>();
        current = Mathf.Clamp(current, 0f, max);
        var maxChanged = !state.HasSnapshot || state.MaxHealth != max;
        var currentChanged = !state.HasSnapshot || state.CurrentHealth != current;
        var tookDamage = state.HasSnapshot && current < state.CurrentHealth;
        state.MaxHealth = max;
        state.CurrentHealth = current;
        state.HasSnapshot = true;

        // Replica stats and base-regeneration effects must not become HP writers.
        HealthTool.FI__current.SetValue(health, current);
        if (maxChanged) health.OnMaxHealthChange?.Invoke(health);
        if (currentChanged) health.OnHealthChange?.Invoke(health);
        if (tookDamage) ShoulderSurfingCompat.NotifyReplicatedHurt(health);
        return true;
    }
}
