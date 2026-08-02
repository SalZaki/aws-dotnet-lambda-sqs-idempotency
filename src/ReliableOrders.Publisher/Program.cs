// The publisher is scaffolded here so the solution layout is settled, but the send commands arrive
// with the demonstration assets in Story 8.2. Failing loudly beats a silent no-op that looks like a
// successful publishing.
await Console.Error.WriteLineAsync(
    "ReliableOrders.Publisher is not implemented yet. See Story 8.2: Add demonstration assets.");

return 1;
