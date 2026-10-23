using FileInviteLite.Api.Common;
using FileInviteLite.Api.Common.Security;
using FileInviteLite.Api.Common.Validation;
using FileInviteLite.Api.Data;
using FileInviteLite.Api.Domain;
using FileInviteLite.Api.Services.Audit;
using FluentValidation;

namespace FileInviteLite.Api.Features.Review;

public sealed class ApproveItem : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/items/{itemId:guid}/approve", Handle)
           .RequireAuthorization(AuthPolicies.Staff)
           .AddEndpointFilter<ValidationFilter<Body>>()
           .WithName(nameof(ApproveItem));

    /// <summary>The comment is optional (S4.4); the body itself is a JSON object, <c>{}</c> at least.</summary>
    public sealed record Body(string? Comment);

    public sealed class Validator : AbstractValidator<Body>
    {
        public Validator()
        {
            RuleFor(b => b.Comment)
                .Must(c => c is null || c.Trim().Length <= ReviewDecisionWriter.MaxCommentLength)
                .WithMessage($"Comment must be {ReviewDecisionWriter.MaxCommentLength} characters or fewer.");
        }
    }

    private static Task<IResult> Handle(
        Guid itemId,
        Body body,
        AppDbContext db,
        IAuditWriter audit,
        CurrentStaff staff,
        TimeProvider clock,
        IConfiguration configuration,
        CancellationToken ct)
    {
        var comment = string.IsNullOrWhiteSpace(body.Comment) ? null : body.Comment.Trim();
        return ReviewDecisionWriter.ApplyAsync(
            itemId, ReviewOutcome.Approve, comment, db, audit, staff.Id, clock, configuration, ct);
    }
}
