using FluentAssertions;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Ln;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Unit;

/// <summary>
/// R9 (D-R9-5) — 4xx permanent / 5xx + timeout + transport retriable, and flip-proof by construction:
/// the classifier's API surface takes only a status code, so no mapping expression output can ever
/// change the class (there is no overload accepting text — that absence IS the invariant).
/// </summary>
public class LnRetriabilityClassifierTests
{
    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(422)]
    [InlineData(499)]
    public void Four_xx_is_permanent(int status) => LnRetriabilityClassifier.IsPermanent(status).Should().BeTrue();

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public void Five_xx_is_retriable(int status) => LnRetriabilityClassifier.IsPermanent(status).Should().BeFalse();

    [Fact]
    public void No_status_code_means_timeout_or_transport_which_is_retriable()
        => LnRetriabilityClassifier.IsPermanent(null).Should().BeFalse();

    [Fact]
    public void Flip_proof_no_overload_accepts_text()
    {
        // The invariant D-R9-5 demands: mappings enrich error TEXT but can never flip the class.
        // Guarded structurally — IsPermanent has no string/expression parameter to influence it.
        var methods = typeof(LnRetriabilityClassifier).GetMethods()
            .Where(m => m.Name == nameof(LnRetriabilityClassifier.IsPermanent));
        methods.Should().OnlyContain(m => m.GetParameters().All(p => p.ParameterType != typeof(string)));
    }
}
