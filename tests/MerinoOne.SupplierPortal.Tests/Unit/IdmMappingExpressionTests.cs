using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Idm;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Unit;

/// <summary>
/// R8 — TSD R8 §4.2 / §4.2b / §10. Output-shape tests: the repo-embedded JSONata expressions, evaluated through
/// the real builder against the spec snapshots, must reproduce the verified IDM envelope exactly (headers, attr
/// array, MDS_AccountingEntity concat, resource base64, pid echo).
/// </summary>
public class IdmMappingExpressionTests
{
    private readonly JsonataOutboundRequestBuilder _builder = new(new MerinoOne.SupplierPortal.Infrastructure.Integration.Ln.LnMappingService());
    private readonly IdmDefaultExpressions _defaults = new();

    [Fact]
    public async Task Invoice_create_reproduces_the_spec_envelope()
    {
        var expr = _defaults.TryGet("InforInvoice")!.CreateExpression;
        var snapshot = new Dictionary<string, object?>
        {
            ["entityType"] = "InforInvoice",
            ["invoice"] = new Dictionary<string, object?>
            {
                ["financialCompany"] = "2000",
                ["logisticCompany"] = "4000",
                ["transactionType"] = "1DS",
                ["lnInvoiceNumber"] = "23063669",
                ["erpCompany"] = "2000",
                ["erpTransactionType"] = "1DS",
                ["erpDocumentNo"] = "23063669",
            },
            ["attachment"] = new Dictionary<string, object?> { ["filename"] = "test.png", ["base64"] = "QUJD" },
            ["config"] = new Dictionary<string, object?> { ["acl"] = "Public", ["entityName"] = "MDS_GenericDocument" },
            ["pid"] = "",
        };

        var envelope = await _builder.BuildAsync(expr, snapshot, CancellationToken.None);

        envelope.Headers["Content-Type"].Should().Be("application/json");
        envelope.Headers["X-Infor-LnCompany"].Should().Be("4000"); // logistic company, distinct from MDS_id1

        using var doc = JsonDocument.Parse(envelope.Body);
        var item = doc.RootElement.GetProperty("item");
        var attrs = item.GetProperty("attrs").GetProperty("attr");

        AttrValue(attrs, "MDS_EntityType").Should().Be("InforInvoice");
        AttrValue(attrs, "MDS_AccountingEntity").Should().Be("infor.ln.2000");
        AttrValue(attrs, "MDS_id1").Should().Be("2000");
        AttrValue(attrs, "MDS_id2").Should().Be("1DS");
        AttrValue(attrs, "MDS_id3").Should().Be("23063669");

        var res = item.GetProperty("resrs").GetProperty("res")[0];
        res.GetProperty("filename").GetString().Should().Be("test.png");
        res.GetProperty("base64").GetString().Should().Be("QUJD");
        item.GetProperty("acl").GetProperty("name").GetString().Should().Be("Public");
        item.GetProperty("entityName").GetString().Should().Be("MDS_GenericDocument");
        item.GetProperty("pid").GetString().Should().Be("");
    }

    [Fact]
    public async Task Asn_create_reproduces_the_spec_envelope()
    {
        var expr = _defaults.TryGet("InforAdvanceShipmentNoticeSupplierASN")!.CreateExpression;
        var snapshot = new Dictionary<string, object?>
        {
            ["entityType"] = "InforAdvanceShipmentNoticeSupplierASN",
            ["asn"] = new Dictionary<string, object?>
            {
                ["financialCompany"] = "1100",
                ["logisticCompany"] = "1100",
                ["transactionType"] = "SUP",
                ["lnDocumentNumber"] = "100000001",
                ["erpCompany"] = "1100",
                ["erpTransactionType"] = "SUP",
                ["erpDocumentNo"] = "100000001",
            },
            ["attachment"] = new Dictionary<string, object?> { ["filename"] = "packing.pdf", ["base64"] = "UERG" },
            ["config"] = new Dictionary<string, object?> { ["acl"] = "Public", ["entityName"] = "MDS_GenericDocument" },
            ["pid"] = "",
        };

        var envelope = await _builder.BuildAsync(expr, snapshot, CancellationToken.None);

        envelope.Headers["X-Infor-LnCompany"].Should().Be("1100");
        using var doc = JsonDocument.Parse(envelope.Body);
        var attrs = doc.RootElement.GetProperty("item").GetProperty("attrs").GetProperty("attr");

        AttrValue(attrs, "MDS_EntityType").Should().Be("InforAdvanceShipmentNoticeSupplierASN");
        AttrValue(attrs, "MDS_AccountingEntity").Should().Be("infor.ln.1100");
        AttrValue(attrs, "MDS_id1").Should().Be("1100");
        AttrValue(attrs, "MDS_id3").Should().Be("100000001");
    }

    [Fact]
    public void Every_embedded_expression_compiles()
    {
        foreach (var entry in _defaults.All)
        {
            _builder.Validate(entry.CreateExpression).Should().BeNull(because: $"{entry.IdmEntityType}.create must compile");
            _builder.Validate(entry.MutateExpression).Should().BeNull(because: $"{entry.IdmEntityType}.mutate must compile");
        }
    }

    private static string? AttrValue(JsonElement attrArray, string name)
    {
        foreach (var a in attrArray.EnumerateArray())
            if (a.GetProperty("name").GetString() == name)
                return a.GetProperty("value").GetString();
        return null;
    }
}
