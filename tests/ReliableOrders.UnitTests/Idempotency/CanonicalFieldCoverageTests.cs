using System.Text.Json.Serialization.Metadata;
using ReliableOrders.Core.Contracts;
using ReliableOrders.Core.Idempotency;

namespace ReliableOrders.UnitTests.Idempotency;

/// <summary>
/// The canonical types mirror the contract types by hand, and nothing in the compiler makes them stay
/// aligned. These tests do.
/// </summary>
/// <remarks>
/// <para>
/// The failure this catches is silent and expensive. Add a field to the contract, forget the canonical
/// type, and two orders that differ on that field hash identically: the second is classified as a
/// duplicate, acknowledged, and its data discarded. Nothing is logged, no alarm fires, and the only
/// evidence is an order in the table with the wrong values.
/// </para>
/// <para>
/// The names are compared through the two serializer contexts rather than through reflection over the
/// records, because the wire name is what a field is called on both sides of the comparison.
/// </para>
/// </remarks>
public sealed class CanonicalFieldCoverageTests
{
    [Fact]
    public void Every_envelope_field_the_contract_reads_is_in_the_canonical_envelope()
    {
        AssertSameFields(
            OrderContractSerializerContext.Default.OrderCreatedV1,
            CanonicalSerializerContext.Default.CanonicalOrderCreatedV1,
            "envelope");
    }

    [Fact]
    public void Every_business_field_the_contract_reads_is_in_the_canonical_data()
    {
        AssertSameFields(
            OrderContractSerializerContext.Default.OrderData,
            CanonicalSerializerContext.Default.CanonicalOrderData,
            "data");
    }

    /// <remarks>
    /// Both directions are checked. A field missing from the canonical type drops out of the hash; a
    /// field present only there hashes something no publisher ever sent, which is just as much a
    /// defect and would go unnoticed for longer.
    /// </remarks>
    private static void AssertSameFields(JsonTypeInfo contract, JsonTypeInfo canonical, string scope)
    {
        var contractFields = WireNames(contract);
        var canonicalFields = WireNames(canonical);

        Assert.True(
            contractFields.SequenceEqual(canonicalFields, StringComparer.Ordinal),
            $"The {scope} contract reads [{string.Join(", ", contractFields)}] and canonicalisation "
            + $"hashes [{string.Join(", ", canonicalFields)}]. A contract field left out of the "
            + "canonical type is dropped from the hash, so two events that differ on it are classified "
            + "as duplicates of one another and the second one's data is discarded. Add the field to "
            + "the canonical type, or record here why it is deliberately excluded — and either way "
            + "treat the change as a schema migration, because every stored hash moves.");
    }

    private static string[] WireNames(JsonTypeInfo typeInfo) =>
        [.. typeInfo.Properties.Select(property => property.Name).Order(StringComparer.Ordinal)];
}
