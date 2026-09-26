namespace Sms.DevSeed;

public static class Cli
{
    public const string ConnectionEnvVar = "SMS_MIGRATOR_CONNECTION";

    public static int Main(string[] args)
    {
        var error = DevSeedGuard.Check(Environment.GetEnvironmentVariable(ConnectionEnvVar), args);
        if (error is not null) { Console.Error.WriteLine(error); return 1; }
        return 0;
    }
}
