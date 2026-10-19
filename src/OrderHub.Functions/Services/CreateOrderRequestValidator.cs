using FluentValidation;
using OrderHub.Contracts;

namespace OrderHub.Functions.Services;

public sealed class CreateOrderRequestValidator : AbstractValidator<CreateOrderRequest>
{
    public CreateOrderRequestValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty().MaximumLength(64);
        RuleFor(x => x.Lines).NotEmpty().WithMessage("An order needs at least one line.");
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.Sku).NotEmpty().MaximumLength(32);
            line.RuleFor(l => l.Quantity).InclusiveBetween(1, 100);
        });
        RuleFor(x => x.ShipToPostalCode).Matches(@"^\d{5}(-\d{4})?$").When(x => x.ShipToPostalCode is not null);
    }
}
