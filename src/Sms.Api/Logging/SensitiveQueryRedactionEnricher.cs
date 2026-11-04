using Serilog.Core;
using Serilog.Events;

namespace Sms.Api.Logging;

/// Runs SensitiveQueryRedactor over every string property of every log event before any sink
/// sees it. The hosting diagnostics log carries the query string as its own {QueryString}
/// property, so replacing the property value also changes the rendered message.
public sealed class SensitiveQueryRedactionEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        List<LogEventProperty>? redacted = null;
        foreach (var (name, value) in logEvent.Properties)
        {
            if (value is not ScalarValue { Value: string s }) continue;
            var clean = SensitiveQueryRedactor.Redact(s);
            if (clean != s)
                (redacted ??= []).Add(new LogEventProperty(name, new ScalarValue(clean)));
        }
        if (redacted is null) return;
        foreach (var p in redacted) logEvent.AddOrUpdateProperty(p);
    }
}
