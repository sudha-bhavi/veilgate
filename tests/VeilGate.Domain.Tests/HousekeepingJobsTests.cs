using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VeilGate.Application.Abstractions;
using VeilGate.Infrastructure.Housekeeping;
using Xunit;

namespace VeilGate.Domain.Tests;

public class HousekeepingJobsTests
{
    private sealed class FakeStore : IHousekeepingStore
    {
        public int VaultRemoved = 3, SessionsRemoved = 2;
        public (TimeSpan Idle, TimeSpan Grace)? LastReap;

        public Task<int> PurgeExpiredVaultAsync(CancellationToken ct = default) => Task.FromResult(VaultRemoved);

        public Task<int> ReapSessionsAsync(TimeSpan idleTimeout, TimeSpan grace, CancellationToken ct = default)
        {
            LastReap = (idleTimeout, grace);
            return Task.FromResult(SessionsRemoved);
        }
    }

    private static (HousekeepingJobs Jobs, FakeStore Store) Build(HousekeepingOptions? options = null)
    {
        var store = new FakeStore();
        var services = new ServiceCollection();
        services.AddScoped<IHousekeepingStore>(_ => store);
        var jobs = new HousekeepingJobs(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options ?? new HousekeepingOptions()), NullLogger<HousekeepingJobs>.Instance);
        return (jobs, store);
    }

    [Fact]
    public async Task Purging_the_vault_reports_how_many_sessions_were_removed()
    {
        var (jobs, _) = Build();

        (await jobs.PurgeVaultAsync(CancellationToken.None)).Should().Be(3);
    }

    [Fact]
    public async Task Reaping_sessions_uses_the_idle_timeout_of_a_session_and_the_configured_grace_period()
    {
        var (jobs, store) = Build(new HousekeepingOptions { SessionGraceHours = 12 });

        (await jobs.ReapSessionsAsync(CancellationToken.None)).Should().Be(2);

        store.LastReap.Should().Be((Session.IdleTimeout, TimeSpan.FromHours(12)));
    }

    [Fact]
    public async Task A_negative_grace_period_is_treated_as_none_not_as_a_way_to_delete_live_sessions()
    {
        var (jobs, store) = Build(new HousekeepingOptions { SessionGraceHours = -5 });

        await jobs.ReapSessionsAsync(CancellationToken.None);

        store.LastReap!.Value.Grace.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void The_default_schedule_is_every_minute_for_the_vault_and_every_five_minutes_for_sessions()
    {
        var options = new HousekeepingOptions();

        (options.VaultPurgeSeconds, options.SessionReapSeconds, options.SessionGraceHours).Should().Be((60, 300, 24));
    }
}
