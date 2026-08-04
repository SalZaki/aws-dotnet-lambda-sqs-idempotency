using ReliableOrders.Core.Contracts;

namespace ReliableOrders.UnitTests.Contracts;

public sealed class OrderContractSerializerContextTests
{
    /// <summary>
    /// The wire name of the schema version field is known in two places: the constant the parser
    /// uses, and the names this context generates from its naming policy. If they diverge, an
    /// unsupported version is reported as a malformed body.
    /// </summary>
    [Fact]
    public void Schema_version_constant_matches_a_generated_property_name()
    {
        var generatedNames = OrderContractSerializerContext.Default.OrderCreatedV1.Properties
            .Select(property => property.Name)
            .ToArray();

        Assert.Contains(OrderContract.SchemaVersionPropertyName, generatedNames);
    }

    /// <summary>
    /// The contract is specified in camelCase, and binding is case-sensitive. Enabling
    /// case-insensitive binding would split the parser's early read from the binder's view of the
    /// same document.
    /// </summary>
    [Fact]
    public void Property_names_are_camel_case()
    {
        var generatedNames = OrderContractSerializerContext.Default.OrderCreatedV1.Properties
            .Select(property => property.Name)
            .ToArray();

        Assert.Contains("eventId", generatedNames);
        Assert.Contains("occurredAtUtc", generatedNames);
        Assert.Contains("causationId", generatedNames);
        Assert.DoesNotContain("EventId", generatedNames);
    }

    [Fact]
    public void Schema_version_spelled_with_the_wrong_case_is_not_the_schema_version()
    {
        var body = Sample.Read(Sample.Valid)
            .Replace("\"schemaVersion\"", "\"SchemaVersion\"", StringComparison.Ordinal);

        var malformed = Assert.IsType<ParseResult.Malformed>(new OrderEventParser().Parse(body));

        Assert.Equal(ParseFailureReason.SchemaVersionUnreadable, malformed.Reason);
    }
}
