using System.Runtime.CompilerServices;

namespace Sms.Tests.Integration;

internal static class TestHostDefaults
{
    // Runs once when the test assembly loads, before any test builds a WebApplicationFactory host.
    // Disables the Transport offline-sweep background worker for every integration-test host: it scans
    // all tenants and auto-ends matching trips against the shared test database, racing with (and
    // emptying the results of) the Transport auto-end tests. Set as an environment variable so the app's
    // default configuration picks it up (TransportOfflineSweep__Enabled -> TransportOfflineSweep:Enabled)
    // without every test's App() helper having to opt out individually. Production is unaffected — the
    // flag defaults on there. EmailDispatchWorker is deliberately left running; several tests rely on it.
    [ModuleInitializer]
    internal static void DisableTransportSweepWorker() =>
        Environment.SetEnvironmentVariable("TransportOfflineSweep__Enabled", "false");
}
