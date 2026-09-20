using FluentAssertions;
using Microsoft.Extensions.Options;
using Sms.Application.Services.AiSearch;
using Sms.Modules.AiSearch.Data;
using Sms.Shared.Kernel.AiSearch;
using Sms.Shared.Kernel.Data;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.AiSearch;

[Collection("sql")]
public class AiConversationContextStoreTests(PostgresFixture fx)
{
    // The repository's queries filter by TenantId/UserId explicitly, so the ambient tenant
    // context only needs to bypass RLS (IsPlatform: true) -- matches the pattern already used by
    // AiSearchLogRepositoryTests and TestTenancy.
    private static AiConversationContextStore MakeStore(FakeTimeProvider clock, string connectionString, int ttlMin = 10, int absMaxMin = 30)
    {
        var ctx = new TenantContext();
        ctx.Set(null, Guid.NewGuid(), true);
        var factory = new NpgsqlConnectionFactory(connectionString, ctx);
        var repo = new AiSearchConversationRepository(factory);
        var options = Options.Create(new AiSearchOptions
        {
            ConversationContextTtlMinutes = ttlMin, ConversationContextAbsoluteMaxMinutes = absMaxMin
        });
        return new AiConversationContextStore(repo, options, clock);
    }

    [Fact]
    public async Task Save_then_load_round_trips_the_resolved_entity()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = MakeStore(clock, fx.ConnectionString);
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var entityId = Guid.NewGuid();

        var conversationId = await store.SaveAsync(null, tenantId, userId,
            new AiConversationContext(entityId, "teacher", null, null, "PersonLookup"));
        var loaded = await store.LoadAsync(conversationId, tenantId, userId);

        loaded.Should().NotBeNull();
        loaded!.ResolvedEntityId.Should().Be(entityId);
        loaded.ResolvedEntityType.Should().Be("teacher");
    }

    [Fact]
    public async Task Load_after_the_sliding_TTL_with_no_activity_returns_null()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = MakeStore(clock, fx.ConnectionString, ttlMin: 10);
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var conversationId = await store.SaveAsync(null, tenantId, userId,
            new AiConversationContext(Guid.NewGuid(), "student", null, null, "PersonLookup"));

        clock.Advance(TimeSpan.FromMinutes(11));

        (await store.LoadAsync(conversationId, tenantId, userId)).Should().BeNull();
    }

    [Fact]
    public async Task Sliding_renewal_keeps_a_fast_back_and_forth_alive_past_the_nominal_TTL()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = MakeStore(clock, fx.ConnectionString, ttlMin: 10, absMaxMin: 30);
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var entityId = Guid.NewGuid();

        var conversationId = await store.SaveAsync(null, tenantId, userId,
            new AiConversationContext(entityId, "student", null, null, "PersonLookup"));

        // Two more turns, 8 minutes apart each (inside the 10-min sliding window each time).
        clock.Advance(TimeSpan.FromMinutes(8));
        (await store.LoadAsync(conversationId, tenantId, userId)).Should().NotBeNull();
        await store.SaveAsync(conversationId, tenantId, userId,
            new AiConversationContext(entityId, "student", null, null, "PersonLookup"));

        clock.Advance(TimeSpan.FromMinutes(8));
        var stillAlive = await store.LoadAsync(conversationId, tenantId, userId);
        stillAlive.Should().NotBeNull("16 minutes have passed in total, past the 10-minute nominal TTL, but each turn was inside it");
    }

    [Fact]
    public async Task Absolute_cap_ends_the_conversation_even_under_continuous_activity()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = MakeStore(clock, fx.ConnectionString, ttlMin: 10, absMaxMin: 30);
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var entityId = Guid.NewGuid();

        var conversationId = await store.SaveAsync(null, tenantId, userId,
            new AiConversationContext(entityId, "student", null, null, "PersonLookup"));

        // Renew every 8 minutes (always inside the sliding TTL) for 32 minutes total -- past the
        // 30-minute absolute cap anchored to CreatedAt.
        for (var i = 0; i < 4; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(8));
            await store.SaveAsync(conversationId, tenantId, userId,
                new AiConversationContext(entityId, "student", null, null, "PersonLookup"));
        }

        (await store.LoadAsync(conversationId, tenantId, userId)).Should().BeNull(
            "the absolute cap must end the conversation regardless of continuous renewal");
    }

    [Fact]
    public async Task SaveAsync_against_an_already_absolute_capped_id_without_a_prior_LoadAsync_mints_a_new_id()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = MakeStore(clock, fx.ConnectionString, ttlMin: 10, absMaxMin: 30);
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var entityId = Guid.NewGuid();

        var originalId = await store.SaveAsync(null, tenantId, userId,
            new AiConversationContext(entityId, "student", null, null, "PersonLookup"));

        // Advance past the absolute cap WITHOUT calling LoadAsync first (which would have deleted
        // the row and forced the caller down a "no conversation" path instead).
        clock.Advance(TimeSpan.FromMinutes(31));

        var renewedId = await store.SaveAsync(originalId, tenantId, userId,
            new AiConversationContext(entityId, "student", null, null, "PersonLookup"));

        renewedId.Should().NotBe(originalId,
            "an already absolute-capped conversation must never be resurrected with a renewed ExpiresAt -- " +
            "SaveAsync must mint a genuinely new conversation instead");

        // The new id is a live, freshly-created conversation.
        (await store.LoadAsync(renewedId, tenantId, userId)).Should().NotBeNull();
    }

    [Fact]
    public async Task A_conversation_id_belonging_to_a_different_user_is_never_returned()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = MakeStore(clock, fx.ConnectionString);
        var tenantId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();

        var conversationId = await store.SaveAsync(null, tenantId, ownerUserId,
            new AiConversationContext(Guid.NewGuid(), "student", null, null, "PersonLookup"));

        (await store.LoadAsync(conversationId, tenantId, otherUserId)).Should().BeNull();
    }

    /// Minimal settable TimeProvider double -- no equivalent existed in this codebase's test
    /// infrastructure (only a fixed, non-advanceable FixedTimeProvider in GreetByIdHandlerTests).
    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }
}
