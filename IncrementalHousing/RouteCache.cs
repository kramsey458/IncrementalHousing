using System;
using System.Collections.Generic;

namespace IncrementalHousing;

// Disposable acceleration data, never part of the planner's saved state or budget.
public sealed class RouteCache<TKey>
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, float> _entries = new Dictionary<TKey, float>();
    public int Count => _entries.Count;
    public RouteCache(int capacity = 8192)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }
    public bool TryGet(TKey key, out float cost) => _entries.TryGetValue(key, out cost);
    public void Store(TKey key, float cost)
    {
        if (cost < 0 || float.IsNaN(cost) || float.IsInfinity(cost)) return;
        if (_entries.Count >= _capacity && !_entries.ContainsKey(key)) _entries.Clear();
        _entries[key] = cost;
    }
    public void Clear() => _entries.Clear();
}
