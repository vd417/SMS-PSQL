using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Sms.Application.Services.Academics;
using Sms.Application.Services.Attendance;
using Sms.Application.Services.Auth;
using Sms.Application.Services.Comms;
using Sms.Application.Services.Dashboard;
using Sms.Application.Services.Finance;
using Sms.Application.Services.Hostel;
using Sms.Application.Services.Issues;
using Sms.Application.Services.Profile;
using Sms.Application.Services.Reporting;
using Sms.Application.Services.Sports;
using Sms.Application.Services.Sis;
using Sms.Application.Services.Staffing;
using Sms.Application.Services.Tenancy;
using Sms.Application.Services.Transport;
using Sms.Application.Services.Users;
using Sms.Application.Services.Realtime;
using Sms.Application.Services.VehicleChecks;

namespace Sms.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IMeSchoolsService, MeSchoolsService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IInvitationService, InvitationService>();
        services.AddScoped<ITenancyService, TenancyService>();
        services.AddScoped<IPlanUpgradeService, PlanUpgradeService>();
        services.AddSingleton<IInvoicePdfGenerator, InvoicePdfGenerator>();
        services.AddSingleton<INoticePdfGenerator, NoticePdfGenerator>();
        services.AddSingleton<IFeeInvoicePdfGenerator, FeeInvoicePdfGenerator>();
        services.AddScoped<IPayrollService, PayrollService>();
        services.AddScoped<ISisService, SisService>();
        services.AddScoped<IStudentBulkImportService, StudentBulkImportService>();
        services.AddScoped<IStaffingService, StaffingService>();
        services.AddScoped<IProfileService, ProfileService>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<IAttendanceViewPermissionService, AttendanceViewPermissionService>();
        services.AddScoped<IAcademicsService, AcademicsService>();
        services.AddScoped<AcademicsCommsNotifier>();
        services.AddScoped<IExamMarksNotifyService, ExamMarksNotifyService>();
        services.AddScoped<IFeeService, FeeService>();
        services.AddScoped<IPayslipService, PayslipService>();
        services.AddScoped<IAttendanceService, AttendanceService>();
        services.AddScoped<IStaffAttendanceService, StaffAttendanceService>();
        services.AddScoped<IAttendanceAlertConfigService, AttendanceAlertConfigService>();
        services.AddScoped<ITripService, TripService>();
        services.AddScoped<FleetSnapshotBuilder>();
        services.AddScoped<ITransportFleetBroadcaster, NoOpTransportFleetBroadcaster>();
        services.AddScoped<IBusService, BusService>();
        services.AddScoped<IStudentBusService, StudentBusService>();
        services.AddScoped<IStudentTransportService, StudentTransportService>();
        services.AddScoped<IBusParentAlertService, BusParentAlertService>();
        services.AddScoped<IBusNotifyService, BusNotifyService>();
        services.AddScoped<IHostelService, HostelService>();
        services.AddScoped<ISportsService, SportsService>();
        services.AddScoped<IThreadService, ThreadService>();
        services.AddScoped<IAnnouncementService, AnnouncementService>();
        services.AddScoped<IComplaintService, ComplaintService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IIssueService, IssueService>();
        services.AddScoped<IVehicleCheckService, VehicleCheckService>();
        services.AddScoped<IUserSettingsService, UserSettingsService>();
        services.AddScoped<IReportingService, ReportingService>();
        services.AddScoped<ILiveBroadcaster, NoOpLiveBroadcaster>();
        return services;
    }
}
