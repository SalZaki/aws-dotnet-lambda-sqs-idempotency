namespace ReliableOrders.Core.Validation;

/// <summary>
/// The complete set of rules an event can break.
/// </summary>
/// <remarks>
/// <para>
/// Low-cardinality and free of payload content, so a rule is safe as a log field and as a metric
/// dimension. The same rule name is reused across fields, which is what lets a dashboard count
/// "required field missing" without one series per field.
/// </para>
/// <para>
/// These read as bare rule names where <see cref="Contracts.ParseFailureReason"/> reads as dotted
/// paths such as <c>json.invalid</c>. The shapes differ because the payloads do: a parse failure has
/// no field to accompany it, so its reason has to carry the location, whereas a
/// <see cref="ValidationFailure"/> already names the field alongside the rule.
/// </para>
/// </remarks>
public static class ValidationRule
{
    /// <summary>
    /// A required field was absent, blank, or a default identifier.
    /// </summary>
    public const string Required = "required";

    /// <summary>
    /// A field exceeded its length limit in <see cref="Contracts.OrderContract"/>.
    /// </summary>
    public const string TooLong = "too-long";

    /// <summary>
    /// A field held a value outside the set this contract allows.
    /// </summary>
    public const string UnexpectedValue = "unexpected-value";

    /// <summary>
    /// A field carried leading or trailing whitespace.
    /// </summary>
    /// <remarks>
    /// Rejected rather than trimmed, and it matters more than it looks. Every text field feeds a
    /// hash: <c>orderId</c> is the domain-level idempotency key, and the rest of <c>data</c> forms
    /// <c>BusinessSha256</c>. A publisher that pads a value on one publish and not on the retry
    /// creates a second order, or turns a benign republish into a conflict with a high-severity
    /// alarm. Trimming here would hide the publisher's bug and change the hash input.
    /// </remarks>
    public const string NotTrimmed = "not-trimmed";

    /// <summary>
    /// A timestamp carried a non-zero UTC offset.
    /// </summary>
    public const string NotUtc = "not-utc";

    /// <summary>
    /// A timestamp fell outside the configured skew window.
    /// </summary>
    public const string OutsideSkewWindow = "outside-skew-window";

    /// <summary>
    /// An amount was zero or negative.
    /// </summary>
    public const string NotPositive = "not-positive";

    /// <summary>
    /// A currency was not three uppercase ASCII letters.
    /// </summary>
    public const string NotACurrencyCode = "not-a-currency-code";
}
