using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Ln;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Unit;

/// <summary>
/// R9 — LnMappingService engine tests, including the two Phase A spike questions the parity design
/// depends on: (1) is a cached <c>JsonataQuery</c> safe under concurrent Eval (singleton registration),
/// and (2) what happens to decimal scale (<c>123.40</c>) through the engine — the canonical writer's
/// trailing-zero normalisation exists because JSONata does not preserve scale.
/// </summary>
public class LnMappingServiceTests
{
    private readonly LnMappingService _svc = new();

    [Fact]
    public void ValidateSyntax_accepts_valid_and_names_invalid()
    {
        _svc.ValidateSyntax("{ \"a\": foo.bar }").Should().BeNull();
        _svc.ValidateSyntax("{{{{ not jsonata").Should().NotBeNullOrEmpty();
        _svc.ValidateSyntax("   ").Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Evaluate_projects_fields()
    {
        var result = _svc.Evaluate("{ \"Out\": inner.value }", "{\"inner\":{\"value\":\"x\"}}");
        result.Ok.Should().BeTrue();
        using var doc = JsonDocument.Parse(result.OutputJson!);
        doc.RootElement.GetProperty("Out").GetString().Should().Be("x");
    }

    [Fact]
    public void Evaluate_undefined_result_is_ok_with_null_output()
    {
        var result = _svc.Evaluate("missing.path", "{\"a\":1}");
        result.Ok.Should().BeTrue();
        result.OutputJson.Should().BeNull();
    }

    [Fact]
    public void Evaluate_reuses_compiled_expression_from_cache()
    {
        var svc = new LnMappingService();
        svc.Evaluate("a + 1", "{\"a\":1}");
        svc.Evaluate("a + 1", "{\"a\":2}");
        svc.CacheCount.Should().Be(1);
        // Leading/trailing whitespace variants of the same expression share one cache entry (normalised hash key).
        svc.Evaluate("a + 1\r\n", "{\"a\":3}").Ok.Should().BeTrue();
        svc.CacheCount.Should().Be(1);
    }

    [Fact]
    public async Task Spike_cached_query_is_safe_under_concurrent_eval()
    {
        // 32 threads × 200 evals over ONE cached compiled query; every result must be exact.
        var expr = "{ \"n\": v * 2, \"s\": name & \"-out\" }";
        var tasks = Enumerable.Range(0, 32).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                var input = $"{{\"v\":{i},\"name\":\"t{t}\"}}";
                var result = _svc.Evaluate(expr, input);
                result.Ok.Should().BeTrue(result.Error);
                using var doc = JsonDocument.Parse(result.OutputJson!);
                doc.RootElement.GetProperty("n").GetInt32().Should().Be(i * 2);
                doc.RootElement.GetProperty("s").GetString().Should().Be($"t{t}-out");
            }
        }));
        await Task.WhenAll(tasks);
        _svc.CacheCount.Should().Be(1);
    }

    [Fact]
    public void Spike_decimal_scale_documented_and_neutralised_by_canonical_writer()
    {
        // The engine is NOT required to preserve decimal scale (123.40 may come back 123.4).
        // The invariant that matters: legacy-STJ output and JSONata output canonicalise to the
        // SAME bytes through LnJson.CanonicalWrite. That is what the parity harness compares and
        // what the dynamic transport POSTs.
        var viaEngine = _svc.Evaluate("{ \"amount\": amount }", "{\"amount\":123.40}");
        viaEngine.Ok.Should().BeTrue();

        var legacyStyle = "{\"amount\":123.40}"; // STJ preserves decimal scale on the legacy path
        LnJson.CanonicalWrite(viaEngine.OutputJson!).Should().Be(LnJson.CanonicalWrite(legacyStyle));
        LnJson.CanonicalWrite(legacyStyle).Should().Be("{\"amount\":123.4}");
    }

    [Fact]
    public void CanonicalWrite_unifies_escaping_and_numbers()
    {
        // Same logical document, different escaping + number scale → identical canonical bytes.
        var a = "{\"s\":\"a<b&c\",\"n\":10.500,\"i\":42,\"arr\":[1.0,2],\"nested\":{\"z\":null}}";
        var b = "{\"s\":\"a\\u003cb\\u0026c\",\"n\":10.5,\"i\":42.000,\"arr\":[1,2.00],\"nested\":{\"z\":null}}";
        LnJson.CanonicalWrite(a).Should().Be(LnJson.CanonicalWrite(b));
    }
}
