using Xunit;

namespace Orion.Api.Tests;

public class OrderTests
{
    [Fact]
    public void Order_total_is_preserved()
    {
        var order = new Order(1, "alice", 42.50m);
        Assert.Equal(42.50m, order.Total);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Order_ids_are_positive(int id) => Assert.True(new Order(id, "bob", 1m).Id > 0);
}
