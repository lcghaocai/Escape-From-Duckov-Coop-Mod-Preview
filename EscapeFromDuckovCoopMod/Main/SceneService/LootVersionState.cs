using System;
using System.Collections.Generic;

namespace EscapeFromDuckovCoopMod;

internal enum LootDeltaDisposition { Apply, Ignore, RequestSnapshot }

// A revision describes an inventory mutation, never a snapshot recipient.
internal sealed class LootVersionState<TKey>
{
    private readonly Dictionary<TKey, int> _versions;
    private readonly Dictionary<TKey, float> _requests;
    internal LootVersionState(IEqualityComparer<TKey> comparer = null)
    {
        _versions = new Dictionary<TKey, int>(comparer);
        _requests = new Dictionary<TKey, float>(comparer);
    }
    internal int Current(TKey key) => _versions.TryGetValue(key, out var value) ? value : 0;
    internal int Advance(TKey key) => _versions[key] = Current(key) + 1;
    internal bool IsStaleSnapshot(TKey key, int version) => version < Current(key);
    internal LootDeltaDisposition ClassifyDelta(TKey key, int version)
    {
        var current = Current(key);
        if (version <= current) return LootDeltaDisposition.Ignore;
        return version == current + 1 ? LootDeltaDisposition.Apply : LootDeltaDisposition.RequestSnapshot;
    }
    internal void Applied(TKey key, int version) => _versions[key] = Math.Max(Current(key), version);
    internal void SnapshotReceived(TKey key) => _requests.Remove(key);
    internal bool TryRequest(TKey key, float now)
    {
        if (_requests.TryGetValue(key, out var sent) && now - sent < 2f) return false;
        _requests[key] = now;
        return true;
    }
    internal TKey[] PendingRequests()
    {
        var keys = new TKey[_requests.Count];
        _requests.Keys.CopyTo(keys, 0);
        return keys;
    }
    internal bool HasPendingRequests => _requests.Count > 0;
    internal void Clear() { _versions.Clear(); _requests.Clear(); }
}
