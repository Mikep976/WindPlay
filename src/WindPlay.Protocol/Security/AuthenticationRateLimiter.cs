using System.Collections.Concurrent;
using System.Net;

namespace AirPlay.Core2.Security;

/// <summary>
/// Bounds online passcode guesses per sender address. Missing Authorization headers are
/// normal Digest negotiation and are not counted as failures.
/// </summary>
public sealed class AuthenticationRateLimiter
{
    internal const int DefaultMaximumFailures = 5;
    internal static readonly TimeSpan DefaultFailureWindow = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan DefaultLockoutDuration = TimeSpan.FromMinutes(5);

    private const int MaximumTrackedAddresses = 1_024;
    private readonly ConcurrentDictionary<IPAddress, AttemptState> _attempts = [];
    private readonly Func<DateTimeOffset> _getUtcNow;
    private readonly int _maximumFailures;
    private readonly TimeSpan _failureWindow;
    private readonly TimeSpan _lockoutDuration;

    public AuthenticationRateLimiter()
        : this(
            static () => DateTimeOffset.UtcNow,
            DefaultMaximumFailures,
            DefaultFailureWindow,
            DefaultLockoutDuration)
    {
    }

    internal AuthenticationRateLimiter(
        Func<DateTimeOffset> getUtcNow,
        int maximumFailures,
        TimeSpan failureWindow,
        TimeSpan lockoutDuration)
    {
        ArgumentNullException.ThrowIfNull(getUtcNow);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFailures, 1);
        if (failureWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(failureWindow));
        if (lockoutDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lockoutDuration));

        _getUtcNow = getUtcNow;
        _maximumFailures = maximumFailures;
        _failureWindow = failureWindow;
        _lockoutDuration = lockoutDuration;
    }

    public bool CanAttempt(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        address = Normalize(address);
        if (!_attempts.TryGetValue(address, out AttemptState? state))
            return true;

        DateTimeOffset now = _getUtcNow();
        lock (state.Gate)
        {
            if (state.BlockedUntil > now)
                return false;

            if (now - state.WindowStartedAt < _failureWindow)
                return true;
        }

        _attempts.TryRemove(new KeyValuePair<IPAddress, AttemptState>(address, state));
        return true;
    }

    /// <returns><see langword="true"/> when another attempt is currently permitted.</returns>
    public bool RecordFailure(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        address = Normalize(address);
        DateTimeOffset now = _getUtcNow();
        AttemptState state = _attempts.GetOrAdd(address, _ => new AttemptState(now));

        bool canRetry;
        lock (state.Gate)
        {
            if (state.BlockedUntil > now)
                return false;

            if (now - state.WindowStartedAt >= _failureWindow)
            {
                state.WindowStartedAt = now;
                state.Failures = 0;
                state.BlockedUntil = default;
            }

            state.Failures++;
            canRetry = state.Failures < _maximumFailures;
            if (!canRetry)
                state.BlockedUntil = now + _lockoutDuration;
        }

        if (_attempts.Count > MaximumTrackedAddresses)
            PruneExpired(now);
        return canRetry;
    }

    public void RecordSuccess(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        _attempts.TryRemove(Normalize(address), out _);
    }

    private void PruneExpired(DateTimeOffset now)
    {
        foreach ((IPAddress address, AttemptState state) in _attempts)
        {
            lock (state.Gate)
            {
                if (state.BlockedUntil > now || now - state.WindowStartedAt < _failureWindow)
                    continue;
            }
            _attempts.TryRemove(new KeyValuePair<IPAddress, AttemptState>(address, state));
        }
    }

    private static IPAddress Normalize(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private sealed class AttemptState(DateTimeOffset windowStartedAt)
    {
        public object Gate { get; } = new();

        public DateTimeOffset WindowStartedAt { get; set; } = windowStartedAt;

        public DateTimeOffset BlockedUntil { get; set; }

        public int Failures { get; set; }
    }
}
