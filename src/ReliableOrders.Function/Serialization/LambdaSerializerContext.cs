using System.Text.Json.Serialization;
using Amazon.Lambda.SQSEvents;

namespace ReliableOrders.Function.Serialization;

/// <summary>
/// Source-generated serialization for everything that crosses the Lambda boundary.
/// </summary>
/// <remarks>
/// <para>
/// Registering the response types is what the Composition Root section of docs/architecture.md
/// insists on, and the reason it gives — that an unregistered type serialises to <c>{}</c>, which
/// Lambda reads as an empty failure list and uses to delete every failed record silently — does not
/// hold on <c>Amazon.Lambda.Serialization.SystemTextJson</c> 3.0.0. Removing the
/// <see cref="SQSBatchResponse"/> registration and serialising produces a
/// <c>JsonSerializerException</c> naming the type. Loud, and it fails the invocation, so the batch is
/// retried rather than lost.
/// </para>
/// <para>
/// The registration stays required and the tests still read bytes. What changed is the severity, not
/// the rule: the failure is a broken deployment rather than silent data loss, and a test that asserts
/// on the returned object would still miss a shape change — a renamed property or a different naming
/// policy writes valid JSON that Lambda cannot match to any record.
/// </para>
/// <para>
/// <see cref="SQSBatchResponse.BatchItemFailure"/> is registered explicitly although the generator
/// reaches it through its container anyway — verified by removing it, where every test still passes.
/// It is kept because the nested type is the one a future change is most likely to strand, and
/// naming it costs an attribute.
/// </para>
/// <para>
/// The inbound contract types are deliberately absent. They are read by
/// <c>OrderContractSerializerContext</c> from the message body, which is a string as far as this
/// boundary is concerned — the runtime deserialises an SQS envelope, not an order.
/// </para>
/// </remarks>
[JsonSerializable(typeof(SQSEvent))]
[JsonSerializable(typeof(SQSEvent.SQSMessage))]
[JsonSerializable(typeof(SQSBatchResponse))]
[JsonSerializable(typeof(SQSBatchResponse.BatchItemFailure))]
public sealed partial class LambdaSerializerContext : JsonSerializerContext;
