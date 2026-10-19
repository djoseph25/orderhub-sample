using OrderHub.Contracts;
using OrderHub.Functions.Services;

namespace OrderHub.Functions.Tests;

[TestClass]
public sealed class CreateOrderRequestValidatorTests
{
    private readonly CreateOrderRequestValidator _validator = new();

    [TestMethod]
    [DataRow(null)]
    [DataRow("12345")]
    [DataRow("12345-6789")]
    public void Accepts_valid_postal_codes(string? postalCode) =>
        Assert.IsTrue(_validator.Validate(new CreateOrderRequest("c-1", [new OrderLine("SKU", 1)], postalCode)).IsValid);

    [TestMethod]
    [DataRow(0)]
    [DataRow(101)]
    public void Rejects_out_of_range_quantity(int quantity)
    {
        var result = _validator.Validate(new CreateOrderRequest("c-1", [new OrderLine("SKU", quantity)], null));

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Errors.Any(e => e.PropertyName == "Lines[0].Quantity"));
    }
}
