using Microsoft.Extensions.DependencyInjection;
using Sms.Modules.Reporting.Data;

namespace Sms.Modules.Reporting;

public static class ReportingModule
{
    public static IServiceCollection AddReportingModule(this IServiceCollection services)
    {
        services.AddScoped<ReportingRepository>();
        services.AddScoped<SchoolReportRepository>();
        return services;
    }
}
