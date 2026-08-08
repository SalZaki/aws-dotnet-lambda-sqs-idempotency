using System.Reflection;
using ReliableOrders.Core.Persistence;

namespace ReliableOrders.UnitTests.Persistence;

/// <summary>
/// The schema constants and the record shapes are written separately and must stay in step.
/// </summary>
/// <remarks>
/// <para>
/// The failure this catches is quiet. Add a property to a record and forget the constant, and the
/// adapter has no name to write it under, so the attribute is silently absent from every stored row.
/// If that property is <c>BusinessSha256</c>, the classification path cannot find the hash it compares
/// on, and a benign republish is reported as a conflict.
/// </para>
/// <para>
/// It also catches the reverse. Rename a C# property and the constant no longer matches, which fails
/// the build — as it should, because rows already written keep the old attribute name and a rename is
/// a migration rather than a refactor.
/// </para>
/// </remarks>
public sealed class PersistenceSchemaTests
{
    [Fact]
    public void The_order_table_names_every_property_of_the_stored_order()
    {
        AssertSchemaMatchesRecord(typeof(OrderTableSchema), typeof(OrderRecord), OrderTableSchema.PartitionKey);
    }

    [Fact]
    public void The_idempotency_table_names_every_property_of_the_stored_record()
    {
        AssertSchemaMatchesRecord(
            typeof(IdempotencyTableSchema),
            typeof(IdempotencyRecord),
            IdempotencyTableSchema.PartitionKey);
    }

    /// <summary>
    /// The partition keys are what the conditional puts guard, so they are asserted by name rather
    /// than left to the aliases above resolving to something plausible.
    /// </summary>
    [Fact]
    public void The_partition_keys_are_the_two_idempotency_keys()
    {
        Assert.Equal(nameof(OrderRecord.OrderId), OrderTableSchema.PartitionKey);
        Assert.Equal(nameof(IdempotencyRecord.IdempotencyKey), IdempotencyTableSchema.PartitionKey);
    }

    /// <summary>
    /// TTL must be configured against the expiry attribute and no other. Pointing it at a timestamp
    /// would delete rows the moment DynamoDB read them as epoch seconds in the past.
    /// </summary>
    [Fact]
    public void The_time_to_live_attribute_is_the_expiry()
    {
        Assert.Equal(
            nameof(IdempotencyRecord.ExpirationEpochSeconds),
            IdempotencyTableSchema.TimeToLiveAttribute);
    }

    private static void AssertSchemaMatchesRecord(Type schema, Type record, string partitionKey)
    {
        var attributeNames = schema
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (string)field.GetValue(null)!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var propertyNames = record
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            attributeNames.SequenceEqual(propertyNames, StringComparer.Ordinal),
            $"{schema.Name} names [{string.Join(", ", attributeNames)}] and {record.Name} carries "
            + $"[{string.Join(", ", propertyNames)}]. A property with no attribute name is silently "
            + "absent from every stored row; an attribute with no property is written by nothing. "
            + "Rows already stored keep the old name, so treat a rename as a migration.");

        Assert.Contains(partitionKey, propertyNames);
    }
}
