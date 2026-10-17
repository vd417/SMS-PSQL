using Microsoft.Extensions.DependencyInjection;
using Sms.Modules.Tenancy.Data;

namespace Sms.Modules.Tenancy;

public static class ModuleEndpoints
{
    public static IServiceCollection AddTenancyModule(this IServiceCollection services)
    {
        services.AddScoped<ClientRepository>();
        services.AddScoped<PlanRepository>();
        services.AddScoped<InvoiceRepository>();
        services.AddScoped<SubscriptionRepository>();
        services.AddScoped<PlanUpgradeRequestRepository>();
        services.AddScoped<SeatRequestRepository>();
        services.AddScoped<DashboardRepository>();
        services.AddScoped<OnboardingRepository>();
        services.AddScoped<TicketRepository>();
        services.AddScoped<TeamRepository>();
        services.AddScoped<AuditRepository>();
        services.AddScoped<ReportRepository>();
        return services;
    }
}
