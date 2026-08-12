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

    // R16.1 (2026-08-11) — the fresh ASN mapping (supersedes the R11.2 INTERIM stub). Keys mirror
    // AsnSnapshotProvider's real output, including the new asn.supplier block that feeds MDS_id1.
    private static Dictionary<string, object?> AsnSnapshot(string pid) => new()
    {
        ["entityType"] = "InforAdvanceShipmentNotice",
        ["asn"] = new Dictionary<string, object?>
        {
            ["financialCompany"] = "2000",
            ["logisticCompany"] = "2000",
            ["companyCode"] = "2000",
            ["asnNumber"] = "ASN-S0013-20260811063857479INB00107",
            ["erpCode"] = "INB00107",
            ["erpSyncId"] = "key-1",
            ["status"] = "Posted",
            ["supplier"] = new Dictionary<string, object?> { ["erpCode"] = "BUS000048", ["supplierCode"] = "S0013" },
        },
        ["attachment"] = new Dictionary<string, object?> { ["filename"] = "PC00000305.pdf", ["base64"] = "UERG" },
        ["config"] = new Dictionary<string, object?> { ["acl"] = "Public", ["entityName"] = "MDS_GenericDocument" },
        ["pid"] = pid,
    };

    [Theory]
    [InlineData("create", "")]
    [InlineData("mutate", "PID-1")]   // Mutate mirrors Create attr-for-attr; only the pid differs at runtime
    public async Task Asn_envelope_carries_the_supplier_asn_erp_id_trio(string slot, string pid)
    {
        var entry = _defaults.TryGet("InforAdvanceShipmentNotice")!;
        var expr = slot == "create" ? entry.CreateExpression : entry.MutateExpression;

        var envelope = await _builder.BuildAsync(expr, AsnSnapshot(pid), CancellationToken.None);

        envelope.Headers["Content-Type"].Should().Be("application/json");
        envelope.Headers["X-Infor-LnCompany"].Should().Be("2000");

        using var doc = JsonDocument.Parse(envelope.Body);
        var item = doc.RootElement.GetProperty("item");
        var attrs = item.GetProperty("attrs").GetProperty("attr");

        AttrValue(attrs, "MDS_EntityType").Should().Be("InforAdvanceShipmentNotice");
        AttrValue(attrs, "MDS_AccountingEntity").Should().Be("infor.ln.2000");
        AttrValue(attrs, "MDS_id1").Should().Be("BUS000048");                             // supplier ERP code
        AttrValue(attrs, "MDS_id2").Should().Be("ASN-S0013-20260811063857479INB00107");   // portal ASN number
        AttrValue(attrs, "MDS_id3").Should().Be("INB00107");                              // LN ASNNo (erpCode)

        var res = item.GetProperty("resrs").GetProperty("res")[0];
        res.GetProperty("filename").GetString().Should().Be("PC00000305.pdf");
        res.GetProperty("base64").GetString().Should().Be("UERG");
        item.GetProperty("acl").GetProperty("name").GetString().Should().Be("Public");
        item.GetProperty("entityName").GetString().Should().Be("MDS_GenericDocument");
        item.GetProperty("pid").GetString().Should().Be(pid);
    }

    [Fact]
    public void Asn_gate_blocks_until_both_the_ln_asn_number_and_the_supplier_erp_code_exist()
    {
        var gate = IdmDefaultExpressions.Seeds["InforAdvanceShipmentNotice"].GateExpr;
        var engine = new MerinoOne.SupplierPortal.Infrastructure.Integration.JsonataEligibilityGate(
            new MerinoOne.SupplierPortal.Infrastructure.Integration.Ln.LnMappingService());

        engine.IsSatisfied(gate, AsnSnapshot("")).Should().BeTrue();

        var noSupplierCode = AsnSnapshot("");
        ((Dictionary<string, object?>)((Dictionary<string, object?>)noSupplierCode["asn"]!)["supplier"]!)["erpCode"] = null;
        engine.IsSatisfied(gate, noSupplierCode).Should().BeFalse();

        var noErpCode = AsnSnapshot("");
        ((Dictionary<string, object?>)noErpCode["asn"]!)["erpCode"] = null;
        engine.IsSatisfied(gate, noErpCode).Should().BeFalse();
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
