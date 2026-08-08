namespace ReliableOrders.Aws.DynamoDb;

/// <summary>
/// The two table names the transaction writes to.
/// </summary>
/// <remarks>
/// <para>
/// Configuration, supplied at the composition root from <c>ORDERS_TABLE_NAME</c> and
/// <c>IDEMPOTENCY_TABLE_NAME</c>. Names differ per environment; the attribute shapes they hold do
/// not, and those live in <c>ReliableOrders.Core.Persistence</c> where the writer, the test harness
/// and the CDK constructs all read them.
/// </para>
/// <para>
/// The properties are get-only and there are no <c>init</c> accessors, so a <c>with</c> expression
/// cannot produce a pair that skipped the constructor's checks.
/// </para>
/// </remarks>
public sealed record DynamoDbTableNames
{
    /// <summary>
    /// Constructs the pair, rejecting a name that would fail every request.
    /// </summary>
    /// <param name="ordersTableName">The orders table.</param>
    /// <param name="idempotencyTableName">The idempotency table.</param>
    /// <exception cref="ArgumentException">
    /// Either name is null, empty or whitespace, or both name the same table.
    /// </exception>
    /// <remarks>
    /// Checked at construction so a missing environment variable fails at the cold start that reads
    /// it, with the offending name in the message, rather than as a <c>ValidationException</c> on
    /// every message once traffic arrives.
    /// </remarks>
    public DynamoDbTableNames(string ordersTableName, string idempotencyTableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ordersTableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyTableName);

        // One table for both rows would make the two conditional puts collide on a single key space,
        // and the transaction would fail as a duplicate item rather than as anything explicable. It is
        // a plausible copy-and-paste error in environment configuration, so it is rejected here.
        if (string.Equals(ordersTableName, idempotencyTableName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The orders and idempotency tables must differ; both were '{ordersTableName}'.",
                nameof(idempotencyTableName));
        }

        OrdersTableName = ordersTableName;
        IdempotencyTableName = idempotencyTableName;
    }

    /// <summary>
    /// The table holding one row per order, keyed by order identifier.
    /// </summary>
    public string OrdersTableName { get; }

    /// <summary>
    /// The table holding one row per event, keyed by event identifier.
    /// </summary>
    public string IdempotencyTableName { get; }
}
