using FluentAssertions;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Idm;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Unit;

/// <summary>R8 — TSD R8 §5.5. Exponential backoff schedule: base·2^(n-1) capped, terminal past maxAttempts.</summary>
public class IdmBackoffPolicyTests
{
    [Theory]
    [InlineData(1, 30)]      // 30·2^0
    [InlineData(2, 60)]      // 30·2^1
    [InlineData(3, 120)]     // 30·2^2
    [InlineData(4, 240)]
    [InlineData(5, 480)]
    public void Backoff_grows_exponentially_from_base(int attempt, int expectedSeconds)
        => IdmBackoffPolicy.NextDelay(attempt, baseSeconds: 30, capSeconds: 3600)
            .TotalSeconds.Should().Be(expectedSeconds);

    [Fact]
    public void Backoff_is_capped()
    {
        IdmBackoffPolicy.NextDelay(20, 30, 3600).TotalSeconds.Should().Be(3600);
        IdmBackoffPolicy.NextDelay(60, 30, 3600).TotalSeconds.Should().Be(3600); // no overflow at high attempts
    }

    [Theory]
    [InlineData(7, 8, false)]
    [InlineData(8, 8, true)]
    [InlineData(9, 8, true)]
    public void Exhaustion_is_at_max_attempts(int attemptCount, int max, bool exhausted)
        => IdmBackoffPolicy.IsExhausted(attemptCount, max).Should().Be(exhausted);
}
