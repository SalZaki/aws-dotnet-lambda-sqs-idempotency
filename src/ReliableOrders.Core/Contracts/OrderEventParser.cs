using System.Diagnostics;
using System.Text.Json;

namespace ReliableOrders.Core.Contracts;

/// <inheritdoc cref="IOrderEventParser"/>
public sealed class OrderEventParser : IOrderEventParser
{
    /// <summary>
    /// A byte order mark decodes to this character. Written as an escape; the character itself is
    /// invisible in an editor and in a diff.
    /// </summary>
    private const char ByteOrderMark = '\uFEFF';

    /// <inheritdoc/>
    public ParseResult Parse(string? messageBody)
    {
        // A BOM is an encoder setting, not content. It cannot affect either hash, since
        // canonicalisation re-serialises from the parsed object, and rejecting it would dead-letter
        // every message from a publisher whose encoder writes one.
        var body = messageBody?.TrimStart(ByteOrderMark);

        if (string.IsNullOrWhiteSpace(body))
        {
            return new ParseResult.Malformed(ParseFailureReason.EmptyBody);
        }

        if (body.Length > OrderContract.MaxMessageBodyCharacters)
        {
            return new ParseResult.Malformed(ParseFailureReason.BodyTooLarge);
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            // Not wrapped: the message carries the payload path and offset. See ParseFailureReason.
            return new ParseResult.Malformed(ParseFailureReason.InvalidJson);
        }

        using (document)
        {
            return ReadEvent(document.RootElement);
        }
    }

    /// <remarks>
    /// Reads the schema version before binding the envelope. A later version may change any field's
    /// shape, so binding first would fail on the shape and report a malformed body rather than an
    /// unsupported version.
    /// </remarks>
    private static ParseResult ReadEvent(JsonElement root)
    {
        if (root.ValueKind is not JsonValueKind.Object)
        {
            return new ParseResult.Malformed(ParseFailureReason.RootNotObject);
        }

        if (!TryReadSchemaVersion(root, out var schemaVersion))
        {
            return new ParseResult.Malformed(ParseFailureReason.SchemaVersionUnreadable);
        }

        if (schemaVersion != OrderContract.SupportedSchemaVersion)
        {
            return new ParseResult.UnsupportedSchemaVersion(schemaVersion);
        }

        try
        {
            // Deserialize is nullable because it returns null for a JSON null literal, which the
            // ValueKind check above already excluded.
            var @event = root.Deserialize(OrderContractSerializerContext.Default.OrderCreatedV1)
                ?? throw new UnreachableException("Binding a JSON object returned null.");

            return new ParseResult.Parsed(@event);
        }
        catch (JsonException)
        {
            return new ParseResult.Malformed(ParseFailureReason.FieldTypeMismatch);
        }
    }

    /// <remarks>
    /// Case-sensitive, matching how the serializer binds the rest of the envelope. The name comes
    /// from <see cref="OrderContract.SchemaVersionPropertyName"/>.
    /// </remarks>
    private static bool TryReadSchemaVersion(JsonElement root, out int schemaVersion)
    {
        schemaVersion = 0;

        return root.TryGetProperty(OrderContract.SchemaVersionPropertyName, out var property)
            && property.ValueKind is JsonValueKind.Number
            && property.TryGetInt32(out schemaVersion);
    }
}
