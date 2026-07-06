using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Integration.Ln;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Unit;

/// <summary>
/// R9 (D-R9-4) — the closed response/ack contract is CLOSED: unknown keys are named errors that block
/// config save; erpKey/erpStatus are mandatory non-empty strings; correlationBag must be an object.
/// </summary>
public class LnClosedContractTests
{
    [Fact]
    public void Minimal_valid_contract_parses()
    {
        var (ack, errors) = LnClosedContract.Parse("{\"erpKey\":\"PO123\",\"erpStatus\":\"Created\"}");
        errors.Should().BeEmpty();
        ack!.ErpKey.Should().Be("PO123");
        ack.ErpStatus.Should().Be("Created");
        ack.Message.Should().BeNull();
        ack.CorrelationBag.Should().BeNull();
    }

    [Fact]
    public void Full_contract_parses_with_bag()
    {
        var (ack, errors) = LnClosedContract.Parse(
            "{\"erpKey\":\"A1\",\"erpStatus\":\"Created\",\"message\":\"ok\",\"correlationBag\":{\"addressCode\":\"AD9\"}}");
        errors.Should().BeEmpty();
        ack!.Message.Should().Be("ok");
        ack.CorrelationBag!.Value.GetProperty("addressCode").GetString().Should().Be("AD9");
    }

    [Fact]
    public void Unknown_key_is_named_and_blocks()
    {
        var (ack, errors) = LnClosedContract.Parse(
            "{\"erpKey\":\"A1\",\"erpStatus\":\"Created\",\"documentNumber\":\"X\"}");
        ack.Should().BeNull();
        errors.Should().ContainSingle(e => e.Contains("'documentNumber'"));
    }

    [Theory]
    [InlineData("{\"erpStatus\":\"Created\"}", "erpKey")]
    [InlineData("{\"erpKey\":\"\",\"erpStatus\":\"Created\"}", "erpKey")]
    [InlineData("{\"erpKey\":\"A1\"}", "erpStatus")]
    [InlineData("{\"erpKey\":\"A1\",\"erpStatus\":42}", "erpStatus")]
    public void Missing_or_wrong_required_fields_block(string json, string expectedField)
    {
        var (ack, errors) = LnClosedContract.Parse(json);
        ack.Should().BeNull();
        errors.Should().Contain(e => e.Contains($"'{expectedField}'"));
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"just a string\"")]
    [InlineData("42")]
    public void Non_object_root_blocks(string json)
    {
        var (ack, errors) = LnClosedContract.Parse(json);
        ack.Should().BeNull();
        errors.Should().ContainSingle(e => e.Contains("object at the root"));
    }

    [Fact]
    public void Non_object_correlation_bag_blocks()
    {
        var (ack, errors) = LnClosedContract.Parse(
            "{\"erpKey\":\"A1\",\"erpStatus\":\"Created\",\"correlationBag\":[1]}");
        ack.Should().BeNull();
        errors.Should().ContainSingle(e => e.Contains("'correlationBag'"));
    }

    [Fact]
    public void Null_and_garbage_input_block()
    {
        LnClosedContract.Parse(null).Errors.Should().NotBeEmpty();
        LnClosedContract.Parse("   ").Errors.Should().NotBeEmpty();
        LnClosedContract.Parse("{not json").Errors.Should().ContainSingle(e => e.Contains("not valid JSON"));
    }
}
