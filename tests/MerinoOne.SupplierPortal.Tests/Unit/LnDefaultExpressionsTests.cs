using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Integration.Ln;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Ln;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Unit;

/// <summary>
/// R9 — the repo LN expression catalogue: every embedded resource loads, every expression compiles,
/// hashes are CRLF-stable, and the starter response/ack expressions produce closed-contract-valid
/// output against their seeded validation samples (so a fresh seed passes save-time validation).
/// </summary>
public class LnDefaultExpressionsTests
{
    private readonly LnDefaultExpressions _catalog = new();
    private readonly LnMappingService _svc = new();

    [Fact]
    public void All_eight_transaction_types_load_with_all_three_slots()
    {
        _catalog.All.Should().HaveCount(8);
        LnDefaultExpressions.PortalEntityByTransactionType.Keys.Should().BeEquivalentTo(
            new[]
            {
                OutboxTransactionType.InvoicePost, OutboxTransactionType.AsnPost,
                OutboxTransactionType.PoAcknowledge, OutboxTransactionType.PoAccept,
                OutboxTransactionType.PoReject, OutboxTransactionType.SupplierChange,
                OutboxTransactionType.SupplierSync, OutboxTransactionType.PoNegotiationApprove,
            });
        foreach (var e in _catalog.All)
        {
            e.RequestExpr.Should().NotBeNullOrWhiteSpace();
            e.ResponseExpr.Should().NotBeNullOrWhiteSpace();
            e.AckExpr.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Every_expression_compiles()
    {
        foreach (var e in _catalog.All)
        {
            _svc.ValidateSyntax(e.RequestExpr).Should().BeNull($"{e.TransactionType} request must compile");
            _svc.ValidateSyntax(e.ResponseExpr).Should().BeNull($"{e.TransactionType} response must compile");
            _svc.ValidateSyntax(e.AckExpr).Should().BeNull($"{e.TransactionType} ack must compile");
        }
        _svc.ValidateSyntax(_catalog.ErrorMessageExpression).Should().BeNull("shared error expression must compile");
    }

    [Fact]
    public void Hashes_are_crlf_stable()
    {
        foreach (var e in _catalog.All)
        {
            ExpressionHash.Compute(e.RequestExpr.Replace("\n", "\r\n")).Should().Be(e.RequestHash);
            ExpressionHash.Compute(e.RequestExpr + "\n").Should().Be(e.RequestHash);
        }
    }

    [Fact]
    public void Starter_response_expressions_satisfy_the_closed_contract_against_the_seeded_sample()
    {
        foreach (var e in _catalog.All)
        {
            var result = _svc.Evaluate(e.ResponseExpr, _catalog.ODataCreatedEntitySample);
            result.Ok.Should().BeTrue(result.Error);
            var (ack, errors) = LnClosedContract.Parse(result.OutputJson);
            errors.Should().BeEmpty($"{e.TransactionType} response starter must be contract-valid");
            ack!.ErpKey.Should().Be("DOC-0001");
            ack.ErpStatus.Should().Be("Created");
        }
    }

    [Fact]
    public void Starter_ack_expressions_satisfy_the_closed_contract_against_the_seeded_sample()
    {
        foreach (var e in _catalog.All)
        {
            var result = _svc.Evaluate(e.AckExpr, _catalog.ErpAckBodySample);
            result.Ok.Should().BeTrue(result.Error);
            var (ack, errors) = LnClosedContract.Parse(result.OutputJson);
            errors.Should().BeEmpty($"{e.TransactionType} ack starter must be contract-valid");
            ack!.ErpKey.Should().Be("INV-23063669");
            ack.ErpStatus.Should().Be("Acked");
            ack.CorrelationBag!.Value.GetProperty("erpDocumentNo").GetString().Should().Be("23063669");
        }
    }

    [Fact]
    public void Error_expression_extracts_odata_error_text()
    {
        var body = "{\"error\":{\"code\":\"400\",\"message\":{\"lang\":\"en\",\"value\":\"Order does not exist\"}}}";
        var result = _svc.Evaluate(_catalog.ErrorMessageExpression, body);
        result.Ok.Should().BeTrue(result.Error);
        result.OutputJson.Should().Be("\"Order does not exist\"");

        var flat = _svc.Evaluate(_catalog.ErrorMessageExpression, "{\"error\":{\"message\":\"boom\"}}");
        flat.OutputJson.Should().Be("\"boom\"");

        var none = _svc.Evaluate(_catalog.ErrorMessageExpression, "{\"unexpected\":true}");
        none.Ok.Should().BeTrue();
        none.OutputJson.Should().BeNull(); // undefined → caller falls back to raw truncation
    }
}
