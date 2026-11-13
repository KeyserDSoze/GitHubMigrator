namespace Orion.Contracts;

public sealed record OrderPlaced(int OrderId, string Customer, decimal Total);
