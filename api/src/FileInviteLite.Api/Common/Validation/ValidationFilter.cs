using FileInviteLite.Api.Common.Results;
using FluentValidation;

namespace FileInviteLite.Api.Common.Validation;

/// <summary>
/// Runs the <see cref="IValidator{T}"/> beside an endpoint before its handler, so a handler never
/// invokes a validator and never sees a body that failed one. Attach with
/// <c>.AddEndpointFilter&lt;ValidationFilter&lt;Body&gt;&gt;()</c>.
/// </summary>
/// <remarks>
/// Failures leave through <see cref="ProblemResults.Validation(IDictionary{string, string[]})"/>,
/// the one error shape in docs/api-contract.md. FluentValidation's own result is never written to
/// a response.
/// </remarks>
/// <typeparam name="T">The request type to validate: a body, or an <c>[AsParameters]</c> record.</typeparam>
public sealed class ValidationFilter<T>(IValidator<T> validator) : IEndpointFilter
    where T : class
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var argument = context.Arguments.OfType<T>().FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"{typeof(ValidationFilter<T>).Name} found no handler argument of type {typeof(T).Name}.");

        var result = await validator.ValidateAsync(argument, context.HttpContext.RequestAborted);
        if (result.IsValid)
        {
            return await next(context);
        }

        var errors = result.Errors
            .GroupBy(failure => ToWireName(failure.PropertyName))
            .ToDictionary(
                group => group.Key,
                group => group.Select(failure => failure.ErrorMessage).Distinct().ToArray());

        return ProblemResults.Validation(errors);
    }

    /// <summary>
    /// <c>Items[0].Title</c> becomes <c>items[0].title</c>: the API speaks camelCase, and the
    /// error keys must match the field names a client sent (S1.2).
    /// </summary>
    private static string ToWireName(string propertyName) =>
        string.Join('.', propertyName.Split('.').Select(segment =>
            segment.Length == 0 ? segment : char.ToLowerInvariant(segment[0]) + segment[1..]));
}
