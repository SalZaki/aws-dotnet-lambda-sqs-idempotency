using Amazon.CDK;
using Amazon.CDK.Assertions;
using Amazon.CDK.AWS.IAM;
using ReliableOrders.Cdk.Configuration;
using ReliableOrders.Cdk.Constructs;

namespace ReliableOrders.CdkTests.Persistence;

/// <summary>
/// What the persistence construct asks CloudFormation to create.
/// </summary>
/// <remarks>
/// The tables are found by partition key rather than by name, because neither carries a physical name
/// and the key is what the runtime writes against.
/// </remarks>
public sealed class PersistenceConstructTests
{
    private const string EnvironmentName = "assert";

    /// <summary>
    /// Both tables are keyed as the event contract requires, and hold no index beyond that.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The schema is asserted to hold one entry, not searched for the key. A sort key on the orders
    /// table would change what the conditional put is conditional on, so a republished order would be
    /// written beside the original instead of colliding with it, and an assertion that only looked for
    /// the partition key somewhere in the schema would not notice. Verified by adding a sort key,
    /// which fails both cases.
    /// </para>
    /// <para>
    /// Both index properties are named. An index nobody queries is billed and written on every put,
    /// and CloudFormation spells them <c>GlobalSecondaryIndexes</c> and <c>LocalSecondaryIndexes</c> —
    /// a key called <c>SecondaryIndexes</c> exists in neither template nor schema, so looking for one
    /// asserts nothing.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(PersistenceConstruct.OrderIdAttribute)]
    [InlineData(PersistenceConstruct.IdempotencyKeyAttribute)]
    public void Every_table_is_keyed_on_its_partition_attribute_alone(string partitionKey)
    {
        var table = Template().TableKeyedOn(partitionKey);

        Assert.Equal("PAY_PER_REQUEST", table.Properties["BillingMode"]);

        var key = Assert.Single(table.Items("KeySchema"));

        Assert.Equal("HASH", key["KeyType"]);
        Assert.Equal(partitionKey, key["AttributeName"]);

        Assert.DoesNotContain("GlobalSecondaryIndexes", table.Properties.Keys, StringComparer.Ordinal);
        Assert.DoesNotContain("LocalSecondaryIndexes", table.Properties.Keys, StringComparer.Ordinal);
    }

    /// <summary>
    /// Both tables are encrypted with a key whose use is recorded.
    /// </summary>
    /// <remarks>
    /// DynamoDB encrypts at rest either way, so this asserts the choice rather than the encryption.
    /// The AWS-owned default leaves no CloudTrail record of key use on a table holding customer
    /// identifiers and amounts, and it emits no <c>SSESpecification</c> at all — the presence of the
    /// property is what distinguishes the two.
    /// </remarks>
    [Theory]
    [InlineData(PersistenceConstruct.OrderIdAttribute)]
    [InlineData(PersistenceConstruct.IdempotencyKeyAttribute)]
    public void Every_table_is_encrypted_with_the_aws_managed_key(string partitionKey)
    {
        Assert.Equal("{\"SSEEnabled\":true}", Template().TableKeyedOn(partitionKey).Json("SSESpecification"));
    }

    /// <summary>
    /// Only the idempotency table expires rows, and it expires them on the documented attribute.
    /// </summary>
    /// <remarks>
    /// The orders table is asserted to have no TTL. Sweeping an order would reopen the order-level
    /// idempotency that the conditional put depends on, so a republished order would be written as a
    /// new one rather than classified as a duplicate.
    /// </remarks>
    [Fact]
    public void Only_the_idempotency_table_expires_its_rows()
    {
        var template = Template();
        var idempotency = template.TableKeyedOn(PersistenceConstruct.IdempotencyKeyAttribute);
        var orders = template.TableKeyedOn(PersistenceConstruct.OrderIdAttribute);

        var ttl = idempotency.Json("TimeToLiveSpecification");

        Assert.Contains($"\"AttributeName\":\"{PersistenceConstruct.ExpirationAttribute}\"", ttl, StringComparison.Ordinal);
        Assert.Contains("\"Enabled\":true", ttl, StringComparison.Ordinal);
        Assert.DoesNotContain("TimeToLiveSpecification", orders.Properties.Keys, StringComparer.Ordinal);
    }

    /// <summary>
    /// Recovery follows the environment rather than the table.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Point_in_time_recovery_follows_the_environment(bool enabled)
    {
        var template = Template(pointInTimeRecovery: enabled);

        foreach (var partitionKey in new[] { PersistenceConstruct.OrderIdAttribute, PersistenceConstruct.IdempotencyKeyAttribute })
        {
            var recovery = template.TableKeyedOn(partitionKey).Json("PointInTimeRecoverySpecification");

            Assert.Contains(
                enabled ? "\"PointInTimeRecoveryEnabled\":true" : "\"PointInTimeRecoveryEnabled\":false",
                recovery,
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Where the data is retained, the tables survive the stack and refuse deletion.
    /// </summary>
    /// <remarks>
    /// The two settings are asserted together because either alone is a half-measure. Deletion
    /// protection without the removal policy leaves CloudFormation failing the delete rather than
    /// keeping the table, and the removal policy without deletion protection leaves the table one
    /// console click from gone.
    /// </remarks>
    [Theory]
    [InlineData(true, "Retain")]
    [InlineData(false, "Delete")]
    public void The_tables_survive_the_stack_where_data_is_retained(bool retainData, string expected)
    {
        var template = Template(retainData);

        foreach (var partitionKey in new[] { PersistenceConstruct.OrderIdAttribute, PersistenceConstruct.IdempotencyKeyAttribute })
        {
            var table = template.TableKeyedOn(partitionKey);

            Assert.Equal(expected, table.DeletionPolicy);
            Assert.Equal(retainData, table.Flag("DeletionProtectionEnabled"));
        }
    }

    /// <summary>
    /// The grant allows writing the two tables and nothing else.
    /// </summary>
    /// <remarks>
    /// The principal is created here because the function that will hold it arrives in Story 4.3. What
    /// is asserted is the shape of the policy the grant writes — one action, two resources — so the
    /// story that wires it up inherits a checked grant rather than an unchecked one.
    /// </remarks>
    [Fact]
    public void The_grant_writes_the_two_tables_and_nothing_else()
    {
        var stack = new Stack(SynthesizedStack.NewApp(), "GrantHarness", new StackProps
        {
            Env = SynthesizedStack.TestEnvironment,
        });

        var persistence = new PersistenceConstruct(stack, "Persistence", Config());

        persistence.GrantOrderTransaction(new Role(stack, "Grantee", new RoleProps
        {
            AssumedBy = new ServicePrincipal("lambda.amazonaws.com"),
        }));

        var template = Amazon.CDK.Assertions.Template.FromStack(stack);
        var policy = template.OnlyResource(SynthesizedStack.IamPolicyResourceType).Json("PolicyDocument");

        Assert.Contains("\"Action\":\"dynamodb:PutItem\"", policy, StringComparison.Ordinal);

        // Named rather than counted. GrantWriteData would satisfy a count of one action and still
        // carry update, delete and batch write.
        foreach (var refused in new[] { "GetItem", "UpdateItem", "DeleteItem", "BatchWriteItem", "Query", "Scan", "dynamodb:*" })
        {
            Assert.DoesNotContain(refused, policy, StringComparison.Ordinal);
        }

        Assert.Contains(template.TableKeyedOn(PersistenceConstruct.OrderIdAttribute).LogicalId, policy, StringComparison.Ordinal);
        Assert.Contains(template.TableKeyedOn(PersistenceConstruct.IdempotencyKeyAttribute).LogicalId, policy, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Resource\":\"*\"", policy, StringComparison.Ordinal);
    }

    /// <summary>
    /// The stack publishes both table names, which is the only way to find a table it did not name.
    /// </summary>
    [Theory]
    [InlineData("OrdersTableName")]
    [InlineData("IdempotencyRecordsTableName")]
    public void The_stack_publishes_the_table_names(string outputName)
    {
        Template().HasOutput(outputName, Match.AnyValue());
    }

    private static Template Template(bool retainData = false, bool pointInTimeRecovery = false) =>
        SynthesizedStack.From(Config(retainData, pointInTimeRecovery));

    private static EnvironmentConfig Config(bool retainData = false, bool pointInTimeRecovery = false) => new(
        environmentName: EnvironmentName,
        lambdaRuntimeIdentifier: "dotnet10",
        lambdaMemoryMb: 512,
        lambdaTimeoutSeconds: 30,
        reservedConcurrency: 10,
        batchSize: 10,
        batchWindowSeconds: 1,
        maxConcurrency: 10,
        visibilityMarginSeconds: 29,
        maxReceiveCount: 5,
        sourceRetentionDays: 4,
        dlqRetentionDays: 14,
        idempotencyRetentionDays: 30,
        retainData: retainData,
        enablePointInTimeRecovery: pointInTimeRecovery,
        alarmThresholds: AlarmThresholds.Development);
}
