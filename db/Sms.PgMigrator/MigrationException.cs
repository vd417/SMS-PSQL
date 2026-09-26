namespace Sms.PgMigrator;

/// Every refusal or failure the runner reports. The message says what happened, what state the
/// database was left in, and what the operator should do next.
public sealed class MigrationException(string message, Exception? inner = null) : Exception(message, inner);
