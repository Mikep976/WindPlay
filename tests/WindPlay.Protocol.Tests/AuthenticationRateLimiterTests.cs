using System.Net;
using AirPlay.Core2.Security;
using Xunit;

namespace WindPlay.Protocol.Tests;

public sealed class AuthenticationRateLimiterTests
{
    private static readonly IPAddress Sender = IPAddress.Parse("192.168.10.25");

    [Fact]
    public void MissingStateAllowsAnAttempt()
    {
        AuthenticationRateLimiter limiter = CreateLimiter(out _);

        Assert.True(limiter.CanAttempt(Sender));
    }

    [Fact]
    public void RepeatedFailuresCauseATemporaryLockout()
    {
        AuthenticationRateLimiter limiter = CreateLimiter(out _);

        Assert.True(limiter.RecordFailure(Sender));
        Assert.True(limiter.RecordFailure(Sender));
        Assert.False(limiter.RecordFailure(Sender));
        Assert.False(limiter.CanAttempt(Sender));
    }

    [Fact]
    public void SuccessfulAuthenticationClearsFailures()
    {
        AuthenticationRateLimiter limiter = CreateLimiter(out _);
        Assert.True(limiter.RecordFailure(Sender));

        limiter.RecordSuccess(Sender);

        Assert.True(limiter.CanAttempt(Sender));
        Assert.True(limiter.RecordFailure(Sender));
    }

    [Fact]
    public void ExpiredLockoutAllowsAnotherWindow()
    {
        AuthenticationRateLimiter limiter = CreateLimiter(out Action<TimeSpan> advance);
        Assert.True(limiter.RecordFailure(Sender));
        Assert.True(limiter.RecordFailure(Sender));
        Assert.False(limiter.RecordFailure(Sender));

        advance(TimeSpan.FromMinutes(6));

        Assert.True(limiter.CanAttempt(Sender));
        Assert.True(limiter.RecordFailure(Sender));
    }

    [Fact]
    public void IPv4MappedAddressSharesTheSameLimit()
    {
        AuthenticationRateLimiter limiter = CreateLimiter(out _);

        Assert.True(limiter.RecordFailure(Sender));
        Assert.True(limiter.RecordFailure(Sender.MapToIPv6()));
        Assert.False(limiter.RecordFailure(Sender));
    }

    private static AuthenticationRateLimiter CreateLimiter(out Action<TimeSpan> advance)
    {
        DateTimeOffset now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        advance = duration => now += duration;
        return new AuthenticationRateLimiter(
            () => now,
            maximumFailures: 3,
            failureWindow: TimeSpan.FromMinutes(5),
            lockoutDuration: TimeSpan.FromMinutes(5));
    }
}
