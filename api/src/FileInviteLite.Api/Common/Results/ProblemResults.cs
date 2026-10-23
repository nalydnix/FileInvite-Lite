namespace FileInviteLite.Api.Common.Results;

/// <summary>
/// The only way a handler should produce an error. Every body here matches the shape in
/// docs/api-contract.md, so no handler has to remember it — and no handler can leak exception
/// text, a stack trace or a file path into a response by improvising one.
/// </summary>
public static class ProblemResults
{
    private const string BaseType = "https://fileinvite-lite/errors/";

    public static IResult Validation(string field, string message) =>
        TypedResults.ValidationProblem(
            new Dictionary<string, string[]> { [field] = [message] },
            detail: message,
            type: BaseType + "validation",
            title: "One or more validation errors occurred.");

    public static IResult Validation(IDictionary<string, string[]> errors) =>
        TypedResults.ValidationProblem(
            errors,
            type: BaseType + "validation",
            title: "One or more validation errors occurred.");

    /// <summary>
    /// Used for a credential that is missing, malformed, expired, revoked or simply wrong. The
    /// message is deliberately identical in all of those cases: telling a caller which one it was
    /// hands them an oracle (S2.6, S8.2).
    /// </summary>
    public static IResult Unauthorized() =>
        TypedResults.Problem(
            detail: "The credentials provided are not valid.",
            statusCode: StatusCodes.Status401Unauthorized,
            title: "Unauthorized",
            type: BaseType + "unauthorized");

    /// <summary>
    /// Also the answer for "exists, but not yours". Returning 403 would confirm that someone
    /// else's resource exists (S2.9).
    /// </summary>
    public static IResult NotFound() =>
        TypedResults.Problem(
            detail: "The requested resource does not exist.",
            statusCode: StatusCodes.Status404NotFound,
            title: "Not Found",
            type: BaseType + "not-found");

    public static IResult Conflict(string detail) =>
        TypedResults.Problem(
            detail: detail,
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict",
            type: BaseType + "conflict");

    public static IResult PayloadTooLarge(string detail) =>
        TypedResults.Problem(
            detail: detail,
            statusCode: StatusCodes.Status413PayloadTooLarge,
            title: "Payload Too Large",
            type: BaseType + "payload-too-large");

    public static IResult UnsupportedMediaType(string detail) =>
        TypedResults.Problem(
            detail: detail,
            statusCode: StatusCodes.Status415UnsupportedMediaType,
            title: "Unsupported Media Type",
            type: BaseType + "unsupported-media-type");
}
