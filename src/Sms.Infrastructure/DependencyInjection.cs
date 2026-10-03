using Microsoft.Extensions.DependencyInjection;
using Sms.Application.Interfaces.DAO;
using Sms.Infrastructure.DAO;

namespace Sms.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureDaos(this IServiceCollection services)
    {
        services.AddScoped<IAuthDao, AuthDao>();
        services.AddScoped<IProfileDao, ProfileDao>();
        services.AddScoped<IUserProvisioningDao, UserProvisioningDao>();
        services.AddScoped<IInvitationDao, InvitationDao>();
        services.AddScoped<IRoleTemplateDao, RoleTemplateDao>();
        services.AddScoped<IContactValidator, ContactValidator>();
        return services;
    }
}
