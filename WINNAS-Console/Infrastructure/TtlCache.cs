namespace Winnas.Console.Infrastructure;

/// <summary>
/// Small in-memory TTL cache. CIM round-trips (especially the first one) can
/// take seconds; the UI polls several pages at once and must not multiply that.
/// </summary>
public sealed class TtlCache
{
    private readonly Dictionary<string, (DateTime At, object Value)> _items = new();
    private readonly object _gate = new();

    public bool TryGet<T>(string key, TimeSpan ttl, out T? value)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < ttl)
            {
                value = (T)hit.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    public void Set<T>(string key, T value)
    {
        lock (_gate) _items[key] = (DateTime.UtcNow, value!);
    }

    public void Invalidate(string? prefix = null)
    {
        lock (_gate)
        {
            if (prefix is null) { _items.Clear(); return; }
            foreach (var k in _items.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                _items.Remove(k);
        }
    }
}
