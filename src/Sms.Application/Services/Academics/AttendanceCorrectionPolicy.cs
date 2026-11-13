namespace Sms.Application.Services.Academics;

/// Saved attendance may be changed by leadership. Two changes in a month are stored only.
/// The third change, and every change after it, emails the school owner.
public static class AttendanceCorrectionPolicy
{
    public const int QuietSavesPerMonth = 2;

    public static bool WarnsOwner(int savesThisMonth) => savesThisMonth > QuietSavesPerMonth;
}
