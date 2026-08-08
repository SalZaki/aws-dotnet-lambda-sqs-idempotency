using ReliableOrders.Aws.DynamoDb;

namespace ReliableOrders.UnitTests.Persistence;

/// <summary>
/// The bounds a mistyped environment variable has to clear.
/// </summary>
/// <remarks>
/// Every check here exists to fail at the cold start that reads the configuration rather than on every
/// message once traffic arrives. An untested guard is how a guard quietly stops working.
/// </remarks>
public sealed class DynamoDbTableNamesTests
{
    [Fact]
    public void Configured_names_are_kept_as_given()
    {
        var tables = new DynamoDbTableNames("orders", "idempotency");

        Assert.Equal("orders", tables.OrdersTableName);
        Assert.Equal("idempotency", tables.IdempotencyTableName);
    }

    /// <summary>
    /// An unset environment variable arrives as null or empty, and whitespace is what a stray quote in
    /// a deployment template leaves behind.
    /// </summary>
    /// <remarks>
    /// <c>ThrowsAny</c> rather than <c>Throws</c>, because a null name raises
    /// <see cref="ArgumentNullException"/> and an empty or whitespace one raises
    /// <see cref="ArgumentException"/>. Both are rejections at the cold start, which is what matters
    /// here; pinning which subtype each case produces would assert a detail of
    /// <c>ArgumentException.ThrowIfNullOrWhiteSpace</c> rather than anything this type promises.
    /// </remarks>
    [Theory]
    [InlineData(null, "idempotency")]
    [InlineData("", "idempotency")]
    [InlineData("   ", "idempotency")]
    [InlineData("orders", null)]
    [InlineData("orders", "")]
    [InlineData("orders", "   ")]
    public void A_missing_name_is_rejected(string? orders, string? idempotency)
    {
        Assert.ThrowsAny<ArgumentException>(() => new DynamoDbTableNames(orders!, idempotency!));
    }

    /// <summary>
    /// One table for both rows would make the two conditional puts collide on a single key space, and
    /// the transaction would fail as a duplicate item rather than as anything explicable. It is a
    /// plausible copy-and-paste error between two environment variables, so it is rejected outright.
    /// </summary>
    [Fact]
    public void Naming_one_table_twice_is_rejected()
    {
        var failure = Assert.Throws<ArgumentException>(() => new DynamoDbTableNames("shared", "shared"));

        Assert.Contains("shared", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The comparison is ordinal, so two tables differing only in case are accepted. DynamoDB table
    /// names are case-sensitive, so they genuinely are two tables.
    /// </summary>
    [Fact]
    public void Names_differing_only_in_case_are_two_tables()
    {
        var tables = new DynamoDbTableNames("Orders", "orders");

        Assert.NotEqual(tables.OrdersTableName, tables.IdempotencyTableName);
    }
}
