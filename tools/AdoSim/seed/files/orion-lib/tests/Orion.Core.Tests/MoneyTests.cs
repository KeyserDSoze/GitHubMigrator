using Orion.Core;
using Xunit;

public class MoneyTests
{
    [Fact]
    public void Adding_same_currency_sums_amounts() =>
        Assert.Equal(15m, Money.Eur(10m).Add(Money.Eur(5m)).Amount);
}
