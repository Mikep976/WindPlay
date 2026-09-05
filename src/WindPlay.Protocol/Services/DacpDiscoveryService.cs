using AirPlay.Core2.Discovery;
using AirPlay.Core2.Models;
using Microsoft.Extensions.Hosting;
using System.Net;

namespace AirPlay.Core2.Services;

public sealed class DacpDiscoveryService(BoundedMdnsService mdns, SessionManager sessions) : BackgroundService
{
    private readonly object _gate = new();
    private readonly Dictionary<DeviceSession, DateTimeOffset> _expiry = [];
    internal int TrackedCount { get { lock (_gate) return _expiry.Count; } }

    internal void Expire(DateTimeOffset now)
    {
        lock (_gate)
            foreach (var entry in _expiry.ToArray())
                if (entry.Value <= now)
                { _expiry.Remove(entry.Key); entry.Key.SetDacpServiceEndPoint(null); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        mdns.PacketReceived += OnPacket;
        sessions.SessionClosed += OnSessionClosed;
        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(stoppingToken))
                Expire(DateTimeOffset.UtcNow);
        }
        finally
        {
            mdns.PacketReceived -= OnPacket;
            sessions.SessionClosed -= OnSessionClosed;
            lock (_gate)
            {
                foreach (var session in _expiry.Keys) session.SetDacpServiceEndPoint(null);
                _expiry.Clear();
            }
        }
    }

    private void OnSessionClosed(object? sender, DeviceSession session)
    { lock (_gate) { _expiry.Remove(session); session.SetDacpServiceEndPoint(null); } }

    internal void OnPacket(DnsPacket packet, IPAddress source)
    {
        foreach (var record in packet.Records)
        {
            const string prefix = "iTunes_Ctrl_", suffix = "._dacp._tcp.local";
            if (record.Type != 33 || record.Port == 0 || !record.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !record.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            string id = record.Name[prefix.Length..^suffix.Length];
            if (id.Length is 0 or > 64 || !id.All(char.IsAsciiLetterOrDigit) ||
                !sessions.TryGetSession(id, out var session) || !session.RemoteAddress.Equals(source)) continue;
            if (!packet.Records.Any(a => a.Type == 1 && a.Name.Equals(record.Target, StringComparison.OrdinalIgnoreCase) &&
                new IPAddress(a.Data).Equals(source))) continue;
            lock (_gate)
            {
                if (record.Ttl == 0) { _expiry.Remove(session); session.SetDacpServiceEndPoint(null); }
                else if (_expiry.ContainsKey(session) || _expiry.Count < 16)
                {
                    _expiry[session] = DateTimeOffset.UtcNow.AddSeconds(Math.Min(record.Ttl, 120));
                    session.SetDacpServiceEndPoint(new(source, record.Port));
                }
            }
        }
    }
}
