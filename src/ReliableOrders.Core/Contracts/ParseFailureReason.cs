namespace ReliableOrders.Core.Contracts;

/// <summary>
/// The complete set of reasons a body can fail to parse.
/// </summary>
/// <remarks>
/// Fixed strings rather than exception messages. A <c>JsonException</c> message carries a JSON path,
/// line number and byte offset, all varying with the payload, which would make the reason unstable as
/// a metric dimension and put attacker-influenced text in the logs. These values are low-cardinality
/// and disclose nothing about the body.
/// </remarks>
public static class ParseFailureReason
{
    /// <summary>
    /// The body was null, empty, or entirely whitespace.
    /// </summary>
    public const string EmptyBody = "body.empty";

    /// <summary>
    /// The body exceeded <see cref="OrderContract.MaxMessageBodyCharacters"/>.
    /// </summary>
    public const string BodyTooLarge = "body.too-large";

    /// <summary>
    /// The body was not well-formed JSON.
    /// </summary>
    public const string InvalidJson = "json.invalid";

    /// <summary>
    /// The body was valid JSON but not an object, so it carries no envelope.
    /// </summary>
    public const string RootNotObject = "json.root-not-object";

    /// <summary>
    /// <c>schemaVersion</c> was absent, or present but not an integer.
    /// </summary>
    public const string SchemaVersionUnreadable = "schema-version.unreadable";

    /// <summary>
    /// A field held a value of the wrong JSON type.
    /// </summary>
    public const string FieldTypeMismatch = "json.field-type-mismatch";
}
