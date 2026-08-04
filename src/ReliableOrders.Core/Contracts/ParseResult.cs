namespace ReliableOrders.Core.Contracts;

/// <summary>
/// The outcome of reading a message body.
/// </summary>
/// <remarks>
/// <para>
/// The <c>private protected</c> constructor prevents another assembly adding a case. It does not make
/// a <c>switch</c> exhaustive: C# has no closed hierarchies, and a switch expression covering all
/// three cases with no discard arm still fails with CS8509.
/// </para>
/// <para>
/// <see cref="Match{TResult}"/> provides that guarantee. Each case is a parameter, so adding one
/// breaks every call site and no site can fall through to a default. Prefer it wherever missing a
/// case would be a correctness bug.
/// </para>
/// </remarks>
public abstract record ParseResult
{
    private protected ParseResult() { }

    /// <summary>
    /// Applies the handler for this result's case.
    /// </summary>
    public abstract TResult Match<TResult>(
        Func<Parsed, TResult> whenParsed,
        Func<Malformed, TResult> whenMalformed,
        Func<UnsupportedSchemaVersion, TResult> whenUnsupportedSchemaVersion);

    /// <summary>
    /// The body was a well-formed event of a supported schema version.
    /// </summary>
    public sealed record Parsed(OrderCreatedV1 Event) : ParseResult
    {
        /// <inheritdoc/>
        public override TResult Match<TResult>(
            Func<Parsed, TResult> whenParsed,
            Func<Malformed, TResult> whenMalformed,
            Func<UnsupportedSchemaVersion, TResult> whenUnsupportedSchemaVersion) => whenParsed(this);
    }

    /// <summary>
    /// The body could not be read as an event of this contract.
    /// </summary>
    /// <param name="Reason">
    /// A stable, body-free description drawn from <see cref="ParseFailureReason"/>, safe to log and
    /// to use as a metric dimension.
    /// </param>
    public sealed record Malformed(string Reason) : ParseResult
    {
        /// <inheritdoc/>
        public override TResult Match<TResult>(
            Func<Parsed, TResult> whenParsed,
            Func<Malformed, TResult> whenMalformed,
            Func<UnsupportedSchemaVersion, TResult> whenUnsupportedSchemaVersion) => whenMalformed(this);
    }

    /// <summary>
    /// Well-formed JSON declaring a schema version this build does not process. Separate from
    /// <see cref="Malformed"/> because the operational response differs: deploy a newer build rather
    /// than fix the publisher.
    /// </summary>
    public sealed record UnsupportedSchemaVersion(int SchemaVersion) : ParseResult
    {
        /// <inheritdoc/>
        public override TResult Match<TResult>(
            Func<Parsed, TResult> whenParsed,
            Func<Malformed, TResult> whenMalformed,
            Func<UnsupportedSchemaVersion, TResult> whenUnsupportedSchemaVersion) =>
            whenUnsupportedSchemaVersion(this);
    }
}
