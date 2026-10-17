using FluentAssertions;
using Sms.Modules.Tenancy.Contracts;
using Xunit;

namespace Sms.Tests.Unit.Tenancy;

/// <summary>
/// Billing-quantity rules for Catre SaaS plans. Verifies the per-student floor is the
/// MINIMUM (not the plan capacity) and that flat plans ignore the student count.
/// </summary>
public class CatreBillingTests
{
    // Platinum: ₹10 / student / month, minimum 100 billable students, capacity 500.
    private static PlanRow Platinum() => new(
        Id: Guid.NewGuid(), Name: "Platinum", Tier: "platinum", Pricing: "per_student",
        Price: 0m, PerStudent: 10m, MinStudents: 100, Period: "month", FeaturesCsv: null,
        LimitsStudents: 500, LimitsStaff: 50, LimitsStorageGb: 50,
        Visibility: "published", Audience: "all", Band: null, OfferLabel: null, OfferPct: null,
        Color: null, Description: null);

    // Silver: flat ₹2,500 / month, capacity 200.
    private static PlanRow Silver() => new(
        Id: Guid.NewGuid(), Name: "Silver", Tier: "silver", Pricing: "flat",
        Price: 2500m, PerStudent: null, MinStudents: null, Period: "month", FeaturesCsv: null,
        LimitsStudents: 200, LimitsStaff: 50, LimitsStorageGb: 30,
        Visibility: "published", Audience: "all", Band: null, OfferLabel: null, OfferPct: null,
        Color: null, Description: null);

    [Theory]
    [InlineData(80, 1000)]   // below minimum -> charged for the 100 minimum
    [InlineData(100, 1000)]  // at minimum
    [InlineData(350, 3500)]  // normal
    [InlineData(600, 6000)]  // above capacity -> overage allowed, billed on actual count
    public void PerStudent_bills_rate_times_max_of_actual_and_minimum(int students, decimal expected)
    {
        CatreMappers.ComputeMonthlyAmount(Platinum(), students, seats: 0)
            .Should().Be(expected);
    }

    [Fact]
    public void PerStudent_capacity_is_not_the_billable_floor()
    {
        // 350 actual students on a plan whose capacity is 500 must bill 350, not 500.
        CatreMappers.BillableSeats(Platinum(), studentsCount: 350, fallbackSeats: 0)
            .Should().Be(350);
        CatreMappers.ComputeMonthlyAmount(Platinum(), 350, seats: 0)
            .Should().Be(3500m);
    }

    [Fact]
    public void PerStudent_uses_committed_count_when_no_live_students_yet()
    {
        // At onboarding/activation a new school has no SIS students; the entered count
        // (carried as the seats fallback) drives the first invoice.
        CatreMappers.ComputeMonthlyAmount(Platinum(), studentsCount: 0, seats: 350)
            .Should().Be(3500m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(150)]   // above the flat plan's capacity
    [InlineData(5000)]
    public void Flat_plan_price_is_fixed_regardless_of_student_count(int students)
    {
        CatreMappers.ComputeMonthlyAmount(Silver(), students, seats: 0)
            .Should().Be(2500m);
    }

    // ---- Billing cycle (monthly / quarterly / yearly) ----

    [Theory]
    [InlineData("month", 1)]
    [InlineData("quarter", 3)]
    [InlineData("year", 12)]
    [InlineData("MONTH", 1)]     // case-insensitive
    [InlineData(null, 1)]        // null -> monthly
    [InlineData("weird", 1)]     // unknown -> monthly (safe default)
    public void MonthsInCycle_maps_cycle_to_month_count(string? cycle, int expected)
    {
        CatreMappers.MonthsInCycle(cycle).Should().Be(expected);
    }

    [Theory]
    [InlineData("month", 3500)]    // 350 x 10 x 1
    [InlineData("quarter", 10500)] // 350 x 10 x 3
    [InlineData("year", 42000)]    // 350 x 10 x 12
    public void PerStudent_cycle_amount_is_monthly_times_months(string cycle, decimal expected)
    {
        CatreMappers.ComputeCycleAmount(Platinum(), studentsCount: 350, cycle: cycle, seats: 0)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData("month", 2500)]
    [InlineData("quarter", 7500)]
    [InlineData("year", 30000)]
    public void Flat_cycle_amount_is_price_times_months(string cycle, decimal expected)
    {
        CatreMappers.ComputeCycleAmount(Silver(), studentsCount: 0, cycle: cycle, seats: 0)
            .Should().Be(expected);
    }
}
