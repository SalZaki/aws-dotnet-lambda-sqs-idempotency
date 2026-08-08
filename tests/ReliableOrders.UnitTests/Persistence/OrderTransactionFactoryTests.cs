using System.Globalization;
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Time.Testing;
using ReliableOrders.Aws.DynamoDb;
using ReliableOrders.Core.Contracts;
using ReliableOrders.Core.Idempotency;
using ReliableOrders.Core.Persistence;
using ReliableOrders.UnitTests.Validation;

namespace ReliableOrders.UnitTests.Persistence;

/// <summary>
/// The transaction request, asserted without a client, a container or a network.
/// </summary>
public sealed class OrderTransactionFactoryTests
{
    private static readonly CanonicalPayloadHasher Hasher = new();

    private static readonly DynamoDbTableNames Tables = new("orders", "idempotency");

    /// <summary>
    /// Index 0 is the idempotency row and index 1 the order. A cancelled transaction reports its
    /// reasons positionally, so swapping these would invert every duplicate and conflict scope without
    /// failing to compile.
    /// </summary>
    [Fact]
    public void The_idempotency_row_is_written_first_and_the_order_second()
    {
        var request = Build(ValidEvent.Create());

        Assert.Equal(2, request.TransactItems.Count);
        Assert.Equal(Tables.IdempotencyTableName, request.TransactItems[0].Put.TableName);
        Assert.Equal(Tables.OrdersTableName, request.TransactItems[1].Put.TableName);
    }

    /// <summary>
    /// Both puts are conditional on the key not existing, which is what makes each a claim rather than
    /// an overwrite. Without it a redelivery would silently replace a stored order and look successful.
    /// </summary>
    [Fact]
    public void Both_puts_are_conditional_on_the_key_not_existing()
    {
        var request = Build(ValidEvent.Create());

        Assert.Equal(
            $"attribute_not_exists({IdempotencyTableSchema.PartitionKey})",
            request.TransactItems[0].Put.ConditionExpression);

        Assert.Equal(
            $"attribute_not_exists({OrderTableSchema.PartitionKey})",
            request.TransactItems[1].Put.ConditionExpression);
    }

    /// <summary>
    /// Both puts return the conflicting row. Classification compares hashes against it and issues no
    /// follow-up read, so without this there is nothing to classify from.
    /// </summary>
    [Fact]
    public void Both_puts_return_the_conflicting_row_on_failure()
    {
        var request = Build(ValidEvent.Create());

        Assert.All(
            request.TransactItems,
            item => Assert.Equal(
                ReturnValuesOnConditionCheckFailure.ALL_OLD,
                item.Put.ReturnValuesOnConditionCheckFailure));
    }

    [Fact]
    public void The_token_is_the_event_id_verbatim()
    {
        var orderEvent = ValidEvent.Create();

        Assert.Equal(orderEvent.EventId.ToString(), Build(orderEvent).ClientRequestToken);
        Assert.Equal(
            OrderWriteRequest.MaxClientRequestTokenLength,
            Build(orderEvent).ClientRequestToken.Length);
    }

    /// <summary>
    /// The acceptance criterion. Two builds of the same event produce the same request body however
    /// much time passes between them.
    /// </summary>
    /// <remarks>
    /// DynamoDB rejects a reused token carrying a different body with
    /// <c>IdempotentParameterMismatchException</c>, so a wall-clock value anywhere in these rows would
    /// turn a routine retry of a valid event into a hard error. The clock is advanced between the two
    /// builds although nothing reads it — the factory takes no <see cref="TimeProvider"/> at all, and a
    /// change that reached for one would have to widen its signature.
    /// </remarks>
    [Fact]
    public void Two_builds_of_the_same_event_are_byte_identical_across_a_moving_clock()
    {
        var orderEvent = ValidEvent.Create();
        var clock = new FakeTimeProvider(ValidEvent.Now);

        var first = Render(Build(orderEvent));
        clock.Advance(TimeSpan.FromDays(2));
        var second = Render(Build(orderEvent));

        Assert.Equal(first, second);

        // Asserted against the event's own timestamp rather than against the clock's. Both happen to
        // fall in the same year, so a check that the rendered body omits "now" would pass or fail on a
        // coincidence rather than on the property being tested.
        Assert.Contains(
            ValidEvent.Create().OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture),
            second,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Every attribute the schema names is written, and nothing else is. An attribute silently absent
    /// would leave stored rows missing a value the classification path or an operator depends on.
    /// </summary>
    [Fact]
    public void Every_schema_attribute_is_written_on_both_rows()
    {
        var request = Build(ValidEvent.Create());

        Assert.Equal(
            SchemaNames(typeof(IdempotencyTableSchema)),
            request.TransactItems[0].Put.Item.Keys.Order(StringComparer.Ordinal));

        Assert.Equal(
            SchemaNames(typeof(OrderTableSchema)),
            request.TransactItems[1].Put.Item.Keys.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The expiry is a number and the timestamps are strings. DynamoDB's time to live only works on a
    /// numeric attribute, so writing the expiry as text disables expiry entirely and silently.
    /// </summary>
    [Fact]
    public void The_expiry_is_numeric_and_the_timestamps_are_text()
    {
        var idempotency = Build(ValidEvent.Create()).TransactItems[0].Put.Item;

        Assert.NotNull(idempotency[IdempotencyTableSchema.ExpirationEpochSeconds].N);
        Assert.Null(idempotency[IdempotencyTableSchema.ExpirationEpochSeconds].S);
        Assert.NotNull(idempotency[IdempotencyTableSchema.OccurredAtUtc].S);
    }

    [Fact]
    public void A_null_argument_throws()
    {
        var write = new OrderWriteRequest(
            ValidEvent.Create(),
            Hasher.ComputeHashes(ValidEvent.Create()),
            IdempotencyRetention.Default);

        Assert.Throws<ArgumentNullException>(() => OrderTransactionFactory.Create(null!, Tables));
        Assert.Throws<ArgumentNullException>(() => OrderTransactionFactory.Create(write, null!));
    }

    private static TransactWriteItemsRequest Build(OrderCreatedV1 orderEvent) =>
        OrderTransactionFactory.Create(
            new OrderWriteRequest(orderEvent, Hasher.ComputeHashes(orderEvent), IdempotencyRetention.Default),
            Tables);

    private static string[] SchemaNames(Type schema) =>
        [.. schema.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(field => (string)field.GetValue(null)!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// Flattens the request to a stable string, so equality is over the bytes that would be sent rather
    /// than over object identity.
    /// </summary>
    private static string Render(TransactWriteItemsRequest request)
    {
        var rendered = new StringBuilder().Append(request.ClientRequestToken);

        foreach (var item in request.TransactItems)
        {
            rendered
                .Append('|').Append(item.Put.TableName)
                .Append('|').Append(item.Put.ConditionExpression)
                .Append('|').Append(item.Put.ReturnValuesOnConditionCheckFailure);

            foreach (var attribute in item.Put.Item.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                rendered
                    .Append('|').Append(attribute.Key)
                    .Append('=').Append(attribute.Value.S ?? attribute.Value.N);
            }
        }

        return rendered.ToString();
    }
}
