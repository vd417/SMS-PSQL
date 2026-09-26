using FluentAssertions;
using Sms.DevSeed;
using Xunit;

namespace Sms.Tests.Unit.DevSeed;

public class SeedIdsTests
{
    [Fact]
    public void Same_key_always_gives_the_same_guid() =>
        SeedIds.Of("teacher.a.user").Should().Be(SeedIds.Of("teacher.a.user"));

    [Fact]
    public void Different_keys_give_different_guids() =>
        SeedIds.Of("teacher.a.user").Should().NotBe(SeedIds.Of("teacher.b.user"));

    [Fact]
    public void Guid_is_stable_across_runs_and_machines() =>
        // Pinned: changing the derivation would orphan every row already seeded into a _dev database.
        SeedIds.Of("tenant.main").Should().Be(Guid.Parse("8ea5da54-aba9-55ab-bac7-662205f376cc"));
}
