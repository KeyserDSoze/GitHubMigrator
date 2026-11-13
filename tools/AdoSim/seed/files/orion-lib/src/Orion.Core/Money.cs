namespace Orion.Core;

public readonly record struct Money(decimal Amount, string Currency)
{
    public static Money Eur(decimal amount) => new(amount, "EUR");

    public Money Add(Money other) =>
        other.Currency == Currency
            ? this with { Amount = Amount + other.Amount }
            : throw new InvalidOperationException("Currency mismatch");
}
