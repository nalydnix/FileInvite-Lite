using FileInviteLite.Api.Common;
using FileInviteLite.Api.Common.Security;
using FileInviteLite.Api.Common.Validation;
using FileInviteLite.Api.Data;
using FileInviteLite.Api.Domain;
using FileInviteLite.Api.Services.Audit;
using FluentValidation;

namespace FileInviteLite.Api.Features.Review;

public sealed class RejectItem : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/items/{itemId:guid}/reject", Handle)
           .RequireAuthorization(AuthPolicies.Staff)
           .AddEndpointFilter<ValidationFilter<Body>>()
           .WithName(nameof(RejectItem));

    public sealed record Body(string? Comment);

    /// <summary>S4.5: required, 1 to 2000 characters after trimming.</summary>
    public sealed class Validator : AbstractValidator<Body>
    {
        public Validator()
        {
            RuleFor(b => b.Comment)
                .Cascade(CascadeMode.Stop)
                .Must(c => !string.IsNullOrWhiteSpace(c)).WithMessage("Comment is required when rejecting an item.")
                .Must(c => c!.Trim().Length <= ReviewDecisionWriter.MaxCommentLength)
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
        CancellationToken ct) =>
        ReviewDecisionWriter.ApplyAsync(
            itemId, ReviewOutcome.Reject, body.Comment!.Trim(), db, audit, staff.Id, clock, configuration, ct);
}
