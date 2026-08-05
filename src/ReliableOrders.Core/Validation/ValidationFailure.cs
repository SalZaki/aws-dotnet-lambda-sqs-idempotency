namespace ReliableOrders.Core.Validation;

/// <summary>
/// One broken contract rule.
/// </summary>
/// <param name="Field">
/// The JSON path of the offending field, as the publisher sent it, so a failure names something the
/// publisher can find. Nested fields are dotted, for example <c>data.orderId</c>.
/// </param>
/// <param name="Rule">
/// Which rule was broken, drawn from <see cref="ValidationRule"/>. Never the offending value: a
/// failure is logged and counted, and the value is the publisher's payload.
/// </param>
public sealed record ValidationFailure(string Field, string Rule);
