using System.Net;

namespace AirPlay.Core2.Security;

/// <summary>Fixed-capacity, per-source and global work admission. No attacker-controlled growth.</summary>
internal sealed class WorkBudget(int globalLimit, int sourceLimit, TimeSpan window, TimeProvider? clock = null)
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<IPAddress, int> _sources = [];
    private DateTimeOffset _reset;
    private int _total;

    public bool TryCharge(IPAddress address, int cost = 1)
    {
        if (cost <= 0) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (now >= _reset)
            {
                _reset = now + window;
                _total = 0;
                _sources.Clear();
            }
            _sources.TryGetValue(address, out int used);
            if (cost > globalLimit - _total || cost > sourceLimit - used ||
                (used == 0 && _sources.Count >= 128)) return false;
            _sources[address] = used + cost;
            _total += cost;
            return true;
        }
    }
}
