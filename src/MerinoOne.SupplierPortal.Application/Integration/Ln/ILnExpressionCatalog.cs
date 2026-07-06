namespace MerinoOne.SupplierPortal.Application.Integration.Ln;

/// <summary>One transaction type's repo-default LN expression triple + normalised hashes (drift/seed handles).</summary>
public sealed record LnExpressionDefault(
    string TransactionType,
    string PortalEntity,
    string RequestExpr,
    string ResponseExpr,
    string AckExpr,
    string RequestHash,
    string ResponseHash,
    string AckHash);

/// <summary>
/// R9 — Application-facing view of the repo LN expression catalogue (mirrors R8's <c>IIdmExpressionCatalog</c>):
/// handlers compute drift flags and restore defaults without referencing Infrastructure.
/// </summary>
public interface ILnExpressionCatalog
{
    LnExpressionDefault? TryGet(string transactionType);
    IReadOnlyCollection<LnExpressionDefault> All { get; }

    /// <summary>Shared default error-text extraction expression (D-R9-5 — text only, never retriability).</summary>
    string ErrorMessageExpression { get; }

    /// <summary>Generic OData created-entity sample (seeds <c>responseSampleJson</c>).</summary>
    string ODataCreatedEntitySample { get; }

    /// <summary>One ErpAckRecord sample as pushed to /inbound/erp-ack (seeds <c>ackSampleJson</c>).</summary>
    string ErpAckBodySample { get; }

    /// <summary>Normalised expression hash (drift = stored text hash ≠ repo default hash).</summary>
    string Hash(string expression);
}
