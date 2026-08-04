namespace ReliableOrders.Core.Contracts;

/// <summary>
/// Fixed values of the order event contract: the version and type this build accepts, and the field
/// length limits. Validation and infrastructure sizing both read the limits from here.
/// </summary>
public static class OrderContract
{
    /// <summary>
    /// The only schema version this build processes. Any other value is rejected.
    /// </summary>
    public const int SupportedSchemaVersion = 1;

    /// <summary>
    /// The only event type this build processes.
    /// </summary>
    public const string ExpectedEventType = "order.created";

    /// <summary>
    /// Wire name of the schema version field.
    /// </summary>
    /// <remarks>
    /// The parser reads this field before binding the envelope, so the name exists both here and in
    /// the names the serializer context generates from its naming policy. A mismatch would report an
    /// unsupported version as a malformed body. OrderContractSerializerContextTests pins the two.
    /// </remarks>
    public const string SchemaVersionPropertyName = "schemaVersion";

    /// <summary>
    /// Length of an ISO 4217 currency code.
    /// </summary>
    public const int CurrencyLength = 3;

    /// <summary>
    /// Maximum length of <see cref="OrderData.OrderId"/>.
    /// </summary>
    public const int MaxOrderIdLength = 64;

    /// <summary>
    /// Maximum length of <see cref="OrderData.CustomerId"/>.
    /// </summary>
    public const int MaxCustomerIdLength = 64;

    /// <summary>
    /// Maximum length of <see cref="OrderCreatedV1.Source"/>.
    /// </summary>
    public const int MaxSourceLength = 128;

    /// <summary>
    /// Maximum length of <see cref="OrderData.ItemDescription"/>.
    /// </summary>
    /// <remarks>
    /// The only field large enough to affect DynamoDB item size. Worst case for the order item, the
    /// larger of the two the transaction writes: order ID 64, customer ID 64, currency 3, amount 20,
    /// description 1024, event ID 36, business hash 64, timestamp 30, TTL 10, and roughly 200 bytes
    /// of attribute names. Under 2 KB against the 400 KB limit. Recalculate before raising this.
    /// </remarks>
    public const int MaxItemDescriptionLength = 1024;

    /// <summary>
    /// Upper bound on a message body.
    /// </summary>
    /// <remarks>
    /// SQS caps a message at 256 KiB of UTF-8, and UTF-8 is never shorter than the string it encodes,
    /// so a body from SQS cannot exceed this. The check guards bodies that did not come from SQS.
    /// </remarks>
    public const int MaxMessageBodyCharacters = 262_144;
}
