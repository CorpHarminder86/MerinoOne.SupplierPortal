namespace MerinoOne.SupplierPortal.Contracts.Dashboard;

/// <summary>
/// A single KPI tile on the dashboard.
/// <para><c>DeltaSign</c> is "+" / "-" / null when <c>PreviousValue</c> is null or zero.</para>
/// </summary>
public record DashboardKpiDto(
    string Label,
    decimal Value,
    decimal? PreviousValue,
    string? DeltaSign);

/// <summary>Lightweight activity row for the dashboard "recent activity" panel.</summary>
public record DashboardActivityDto(
    string Module,
    string Title,
    string Status,
    DateTime When);

/// <summary>Aggregate dashboard payload: KPI tiles + recent activity feed.</summary>
public record DashboardSummaryDto(
    IReadOnlyList<DashboardKpiDto> Kpis,
    IReadOnlyList<DashboardActivityDto> RecentActivity);
