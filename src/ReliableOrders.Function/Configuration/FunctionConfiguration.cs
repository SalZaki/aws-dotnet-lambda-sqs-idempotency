using System.Globalization;
using Microsoft.Extensions.Logging;
using ReliableOrders.Core.Idempotency;
using ReliableOrders.Core.Validation;

namespace ReliableOrders.Function.Configuration;

/// <summary>
/// Everything this function reads from its environment, checked once at cold start.
/// </summary>
/// <remarks>
/// <para>
/// Read and validated together, before anything is constructed, so a missing or unusable variable
/// fails the initialisation that reads it rather than the first message that needs it. The difference
/// matters: a cold start that throws is one clear failure in the logs, while a lazily-read variable
/// produces a per-message error whose cause is a deployment made hours earlier.
/// </para>
/// <para>
/// Every message names the variable it is about. An operator reading <c>ORDERS_TABLE_NAME is not
/// set</c> knows what to do; one reading <c>Value cannot be null</c> has to go and find out which
/// value.
/// </para>
/// <para>
/// Required and optional are decided by what a wrong value costs. Table names, environment, service
/// and namespace are required because nothing sensible can be assumed: a defaulted table name writes
/// orders somewhere nobody is looking, and a defaulted service or environment mislabels every metric
/// in the account. The tuning values are optional, because the types they build already carry
/// defaults that the specification chose.
/// </para>
/// </remarks>
public sealed record FunctionConfiguration
{
    /// <summary>The table holding one row per order.</summary>
    public const string OrdersTableNameVariable = "ORDERS_TABLE_NAME";

    /// <summary>The table holding one row per event.</summary>
    public const string IdempotencyTableNameVariable = "IDEMPOTENCY_TABLE_NAME";

    /// <summary>How long an idempotency record is kept. Optional.</summary>
    public const string IdempotencyRetentionDaysVariable = "IDEMPOTENCY_RETENTION_DAYS";

    /// <summary>The service name, which is a dimension on every metric.</summary>
    public const string ServiceNameVariable = "POWERTOOLS_SERVICE_NAME";

    /// <summary>The deployment environment, which is the other dimension.</summary>
    public const string EnvironmentVariable = "ENVIRONMENT";

    /// <summary>The CloudWatch namespace every metric is published under.</summary>
    public const string MetricsNamespaceVariable = "METRICS_NAMESPACE";

    /// <summary>The lowest level written. Optional.</summary>
    public const string LogLevelVariable = "LOG_LEVEL";

    /// <summary>How far ahead of now an event may claim to have occurred. Optional.</summary>
    public const string MaxEventSkewFutureHoursVariable = "MAX_EVENT_SKEW_FUTURE_HOURS";

    /// <summary>How far behind now an event may claim to have occurred. Optional.</summary>
    public const string MaxEventSkewPastDaysVariable = "MAX_EVENT_SKEW_PAST_DAYS";

    private FunctionConfiguration(
        string ordersTableName,
        string idempotencyTableName,
        IdempotencyRetention retention,
        string serviceName,
        string environment,
        string metricsNamespace,
        LogLevel logLevel,
        EventSkewWindow skewWindow)
    {
        OrdersTableName = ordersTableName;
        IdempotencyTableName = idempotencyTableName;
        Retention = retention;
        ServiceName = serviceName;
        Environment = environment;
        MetricsNamespace = metricsNamespace;
        LogLevel = logLevel;
        SkewWindow = skewWindow;
    }

    /// <summary>The table holding one row per order.</summary>
    public string OrdersTableName { get; }

    /// <summary>The table holding one row per event.</summary>
    public string IdempotencyTableName { get; }

    /// <summary>How long an idempotency record is kept.</summary>
    public IdempotencyRetention Retention { get; }

    /// <summary>The service name, a dimension on every metric and a field on every log line.</summary>
    public string ServiceName { get; }

    /// <summary>The deployment environment.</summary>
    public string Environment { get; }

    /// <summary>The CloudWatch namespace every metric is published under.</summary>
    public string MetricsNamespace { get; }

    /// <summary>The lowest level written.</summary>
    public LogLevel LogLevel { get; }

    /// <summary>How far an event's stated time may differ from now.</summary>
    public EventSkewWindow SkewWindow { get; }

    /// <summary>
    /// Reads the configuration from the process environment.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A required variable is absent or blank, or a value cannot be used. The message names the
    /// variable.
    /// </exception>
    public static FunctionConfiguration FromEnvironment() =>
        From(name => System.Environment.GetEnvironmentVariable(name));

    /// <summary>
    /// Reads the configuration from an arbitrary source, which is what lets it be tested.
    /// </summary>
    /// <param name="read">Returns a variable's value, or null when it is not set.</param>
    /// <inheritdoc cref="FromEnvironment"/>
    public static FunctionConfiguration From(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        var ordersTableName = Required(read, OrdersTableNameVariable);
        var idempotencyTableName = Required(read, IdempotencyTableNameVariable);

        // Checked here rather than left to DynamoDbTableNames, which reports it as an ArgumentException
        // naming a constructor parameter. One table for both rows makes the two conditional puts
        // collide on a single key space, and it is a plausible copy-and-paste between two environment
        // variables — so the message names both of them.
        if (string.Equals(ordersTableName, idempotencyTableName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{OrdersTableNameVariable} and {IdempotencyTableNameVariable} are both set to "
                + $"'{ordersTableName}'. They must name different tables.");
        }

        return new FunctionConfiguration(
            ordersTableName,
            idempotencyTableName,
            Retained(read),
            Required(read, ServiceNameVariable),
            Required(read, EnvironmentVariable),
            Required(read, MetricsNamespaceVariable),
            Level(read),
            Skew(read));
    }

    private static string Required(Func<string, string?> read, string name)
    {
        var value = read(name);

        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{name} is not set. It has no default and must be configured.")
            : value;
    }

    /// <remarks>
    /// The optional values are parsed through here so an unusable one fails the same way a missing
    /// required one does, naming itself. Falling back to a default on a value someone deliberately
    /// set would run the service on a number nobody chose.
    /// </remarks>
    private static double? OptionalNumber(Func<string, string?> read, string name)
    {
        var value = read(name);

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"{name} is set to '{value}', which is not a number.");
    }

    private static IdempotencyRetention Retained(Func<string, string?> read)
    {
        var days = OptionalNumber(read, IdempotencyRetentionDaysVariable);

        return days is null
            ? IdempotencyRetention.Default
            : Rejecting(
                IdempotencyRetentionDaysVariable,
                days.Value,
                () => new IdempotencyRetention(TimeSpan.FromDays(days.Value)));
    }

    /// <remarks>
    /// The two bounds are converted separately, each inside its own wrapper, so a value out of range
    /// is reported against the variable it came from. Validating them together named whichever was
    /// checked first and quoted its value, which sent an operator to change a variable that was
    /// already correct while the cold start kept failing.
    /// </remarks>
    private static EventSkewWindow Skew(Func<string, string?> read)
    {
        var future = OptionalNumber(read, MaxEventSkewFutureHoursVariable);
        var past = OptionalNumber(read, MaxEventSkewPastDaysVariable);

        if (future is null && past is null)
        {
            return EventSkewWindow.Default;
        }

        var maxFuture = future is null
            ? EventSkewWindow.Default.MaxFuture
            : Rejecting(MaxEventSkewFutureHoursVariable, future.Value, () => TimeSpan.FromHours(future.Value));

        var maxPast = past is null
            ? EventSkewWindow.Default.MaxPast
            : Rejecting(MaxEventSkewPastDaysVariable, past.Value, () => TimeSpan.FromDays(past.Value));

        // The window's own bounds are checked last, and can only fail on a value one of the two
        // variables supplied, so the message names both rather than guessing between them.
        return Rejecting(
            $"{MaxEventSkewFutureHoursVariable} and {MaxEventSkewPastDaysVariable}",
            $"{maxFuture} and {maxPast}",
            () => new EventSkewWindow(maxFuture, maxPast));
    }

    private static LogLevel Level(Func<string, string?> read)
    {
        var value = read(LogLevelVariable);

        if (string.IsNullOrWhiteSpace(value))
        {
            return LogLevel.Information;
        }

        // IsDefined as well as TryParse. TryParse alone accepts a number, so LOG_LEVEL=6 becomes None
        // and LOG_LEVEL=99 becomes an undefined value — both of which make SetMinimumLevel suppress
        // every line the service writes. A mistyped variable would leave a function running with no
        // observability at all and nothing to point at, which is the failure this whole type exists to
        // prevent.
        if (!Enum.TryParse<LogLevel>(value, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
        {
            throw new InvalidOperationException(
                $"{LogLevelVariable} is set to '{value}', which is not one of "
                + $"{string.Join(", ", Enum.GetNames<LogLevel>())}.");
        }

        // None is a defined value and still means silence. Refused explicitly, because a service that
        // writes nothing cannot be operated and the alarms are built on what it writes.
        return parsed == LogLevel.None
            ? throw new InvalidOperationException(
                $"{LogLevelVariable} is set to '{value}', which would write no logs at all. "
                + $"Use {nameof(LogLevel.Critical)} if that is the intent.")
            : parsed;
    }

    /// <remarks>
    /// The bounds these values must satisfy belong to the types that hold them, and both report a
    /// violation as an <see cref="ArgumentException"/> naming a constructor parameter no operator has
    /// heard of. Rethrown here against the variable the value came from, with the original kept as the
    /// inner exception so the bound itself is not lost.
    /// </remarks>
    private static T Rejecting<T>(string name, double value, Func<T> construct) =>
        Rejecting(name, value.ToString(CultureInfo.InvariantCulture), construct);

    /// <remarks>
    /// <para>
    /// <see cref="OverflowException"/> as well as <see cref="ArgumentException"/>. A double large
    /// enough to overflow a <see cref="TimeSpan"/>, or an infinity, parses happily and then throws the
    /// former — which is not an argument exception, so catching only that let a value out of range
    /// escape naming nothing. Both are the same thing to an operator: a number the service cannot use.
    /// </para>
    /// <para>
    /// The conversion has to happen inside <paramref name="construct"/>. Building the
    /// <see cref="TimeSpan"/> before the call put the throw outside the wrapper, which is how
    /// <c>NaN</c> escaped as a bare <see cref="ArgumentException"/>.
    /// </para>
    /// </remarks>
    private static T Rejecting<T>(string name, string value, Func<T> construct)
    {
        try
        {
            return construct();
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            throw new InvalidOperationException(
                $"{name} is set to '{value}', which is out of range.",
                exception);
        }
    }
}
