namespace ReliableOrders.Core.Contracts;

/// <summary>
/// Versioned envelope for one order creation event. Specified in docs/event-contract.md.
/// </summary>
/// <remarks>
/// Properties are non-nullable although a message may omit any of them. This type is the output of
/// parsing, not validation: a missing string arrives as null, a missing identifier as
/// <see cref="Guid.Empty"/>, and the validator reports it as a failure naming the field. Declaring
/// them nullable would spread null handling across every consumer for a state that never survives
/// validation.
/// </remarks>
/// <param name="SchemaVersion">Must equal <see cref="OrderContract.SupportedSchemaVersion"/>.</param>
/// <param name="EventId">Identifies this event, and is the event-level idempotency key.</param>
/// <param name="EventType">Must equal <see cref="OrderContract.ExpectedEventType"/>.</param>
/// <param name="OccurredAtUtc">
/// When the event happened. Must be UTC. A non-zero offset is rejected rather than normalised, since
/// normalising would change the hash input.
/// </param>
/// <param name="Source">Publisher identity, for diagnostics.</param>
/// <param name="CorrelationId">Shared by every event in one logical flow.</param>
/// <param name="CausationId">
/// The event that caused this one, or null for a root event. Included in the envelope hash, so two
/// events differing only here are distinct.
/// </param>
/// <param name="Data">The business payload, and the sole input to the domain-level hash.</param>
public sealed record OrderCreatedV1(
    int SchemaVersion,
    Guid EventId,
    string EventType,
    DateTimeOffset OccurredAtUtc,
    string Source,
    Guid CorrelationId,
    Guid? CausationId,
    OrderData Data);
