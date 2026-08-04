namespace ReliableOrders.Core.Contracts;

/// <summary>
/// Reads a raw message body into a typed event, or reports why it could not.
/// </summary>
/// <remarks>
/// Parsing establishes only that the body is an order event of a known version. Whether its values
/// are acceptable is validation's question, kept separate so a malformed body and a negative amount
/// are classified differently.
/// </remarks>
public interface IOrderEventParser
{
    /// <summary>
    /// Never throws for bad input. Every failure is returned as a <see cref="ParseResult"/>.
    /// </summary>
    /// <param name="messageBody">Raw body. Nullable because an SQS record can carry a null body.</param>
    ParseResult Parse(string? messageBody);
}
