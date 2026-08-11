using Amazon.CDK;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.IAM;
using Constructs;
using ReliableOrders.Cdk.Configuration;
using Attribute = Amazon.CDK.AWS.DynamoDB.Attribute;

namespace ReliableOrders.Cdk.Constructs;

/// <summary>
/// The orders table and the idempotency table, which are written by one transaction.
/// </summary>
/// <remarks>
/// <para>
/// Neither table is given a physical name. The queues in <see cref="MessagingConstruct"/> are named
/// because a redrive allow policy has to name one of them, and nothing here has that constraint. A
/// generated name costs a CloudFormation output and buys back the ability to replace a table without
/// a name collision.
/// </para>
/// <para>
/// Both tables are on-demand and hold no secondary index. The only access patterns are a conditional
/// put per table inside <c>TransactWriteItems</c> and, on failure, the old image DynamoDB returns
/// with the cancellation reason. Nothing reads by anything other than the partition key.
/// </para>
/// </remarks>
public sealed class PersistenceConstruct : Construct
{
    /// <summary>The partition key of the orders table.</summary>
    public const string OrderIdAttribute = "OrderId";

    /// <summary>The partition key of the idempotency table, which is the event ID verbatim.</summary>
    public const string IdempotencyKeyAttribute = "IdempotencyKey";

    /// <summary>The attribute DynamoDB reads to expire an idempotency record.</summary>
    public const string ExpirationAttribute = "ExpirationEpochSeconds";

    /// <summary>
    /// Creates both tables.
    /// </summary>
    /// <param name="scope">The stack or construct these tables belong to.</param>
    /// <param name="id">The construct identifier, which prefixes both logical IDs.</param>
    /// <param name="config">Decides recovery, deletion protection and what happens on stack deletion.</param>
    public PersistenceConstruct(Construct scope, string id, EnvironmentConfig config)
        : base(scope, id)
    {
        ArgumentNullException.ThrowIfNull(config);

        Orders = new Table(this, "OrdersTable", Common(config, OrderIdAttribute));

        // The idempotency table is the only one with a TTL. An order is not expired by anything, and
        // sweeping one would silently reopen the order-level idempotency the conditional put depends
        // on.
        IdempotencyRecords = new Table(
            this,
            "IdempotencyRecordsTable",
            Common(config, IdempotencyKeyAttribute, ExpirationAttribute));
    }

    /// <summary>One row per order, keyed by <see cref="OrderIdAttribute"/>.</summary>
    public ITable Orders { get; }

    /// <summary>One row per event, keyed by <see cref="IdempotencyKeyAttribute"/>.</summary>
    public ITable IdempotencyRecords { get; }

    /// <summary>
    /// Allows a principal to write the two rows of the order transaction, and nothing else.
    /// </summary>
    /// <param name="grantee">The function's execution role.</param>
    /// <remarks>
    /// <para>
    /// <c>dynamodb:PutItem</c> on the two table ARNs is the whole permission. A transactional write is
    /// authorised by the actions of the items inside it, so <c>TransactWriteItems</c> needs no grant of
    /// its own, and the design reads nothing back — the old image used for classification arrives in
    /// the cancellation reason rather than from a follow-up <c>GetItem</c>.
    /// </para>
    /// <para>
    /// One statement over both ARNs rather than <c>ITable.GrantWriteData</c> on each. That method also
    /// grants update, delete and batch write, and index ARNs this schema has no indexes for.
    /// </para>
    /// <para>
    /// <c>AssertSuccess</c> because <c>AddToPrincipal</c> does not fail on a principal it cannot write
    /// to. A role imported without <c>mutable</c> takes the grant, records a warning, and synthesises
    /// a template with no policy in it — which is a function that deploys and then throws
    /// <c>AccessDenied</c> on its first transaction.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">The grantee is null.</exception>
    public Grant GrantOrderTransaction(IGrantable grantee)
    {
        ArgumentNullException.ThrowIfNull(grantee);

        var grant = Grant.AddToPrincipal(new GrantOnPrincipalOptions
        {
            Grantee = grantee,
            Actions = ["dynamodb:PutItem"],
            ResourceArns = [Orders.TableArn, IdempotencyRecords.TableArn],
        });

        grant.AssertSuccess();

        return grant;
    }

    /// <summary>
    /// Builds a table's properties, so the settings both tables share cannot drift apart.
    /// </summary>
    /// <param name="config">Decides recovery, deletion protection and what happens on stack deletion.</param>
    /// <param name="partitionKey">The table's only key.</param>
    /// <param name="timeToLiveAttribute">The attribute rows expire on, where the table expires rows.</param>
    /// <remarks>
    /// <para>
    /// Recovery, deletion protection and removal policy all follow the environment. A development
    /// stack is meant to be thrown away and recreated, and a production one is meant to survive a
    /// mistake in this repository — the same table declaration has to do both.
    /// </para>
    /// <para>
    /// The two varying values are parameters rather than a <see cref="TableProps"/> the caller fills
    /// in. Taking props and copying two fields out of it compiles for every field it drops, so a sort
    /// key or a stream set at a call site would synthesise and deploy as nothing.
    /// </para>
    /// </remarks>
    private static TableProps Common(
        EnvironmentConfig config,
        string partitionKey,
        string? timeToLiveAttribute = null) => new()
        {
            PartitionKey = new Attribute { Name = partitionKey, Type = AttributeType.STRING },
            TimeToLiveAttribute = timeToLiveAttribute,

            BillingMode = BillingMode.PAY_PER_REQUEST,

            // AWS-managed rather than the AWS-owned default. Both encrypt at rest, and only this one
            // records key use in CloudTrail, which is what an auditor asks for on a table holding customer
            // identifiers and amounts. It is not a customer-managed key, so the execution role needs no
            // KMS permissions of its own.
            Encryption = TableEncryption.AWS_MANAGED,

            PointInTimeRecoverySpecification = new PointInTimeRecoverySpecification
            {
                PointInTimeRecoveryEnabled = config.EnablePointInTimeRecovery,
            },

            DeletionProtection = config.RetainData,
            RemovalPolicy = config.RetainData ? RemovalPolicy.RETAIN : RemovalPolicy.DESTROY,
        };
}
