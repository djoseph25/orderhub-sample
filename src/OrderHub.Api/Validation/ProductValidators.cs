using FluentValidation;
using OrderHub.Contracts;

namespace OrderHub.Api.Validation;

public sealed class CreateProductRequestValidator : AbstractValidator<CreateProductRequest>
{
    public CreateProductRequestValidator()
    {
        RuleFor(x => x.Sku).NotEmpty().MaximumLength(32).Matches("^[A-Z0-9-]+$").WithMessage("SKU must be upper-case letters, digits or dashes.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Price).GreaterThan(0).PrecisionScale(18, 2, ignoreTrailingZeros: true);
    }
}

public sealed class UpdateProductRequestValidator : AbstractValidator<UpdateProductRequest>
{
    public UpdateProductRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Price).GreaterThan(0).PrecisionScale(18, 2, ignoreTrailingZeros: true);
        RuleFor(x => x.Version).NotEmpty();
    }
}
