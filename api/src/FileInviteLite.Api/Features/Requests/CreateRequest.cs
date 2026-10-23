using System.Net.Mail;
using FileInviteLite.Api.Common;
using FileInviteLite.Api.Common.Security;
using FileInviteLite.Api.Common.Validation;
using FileInviteLite.Api.Data;
using FileInviteLite.Api.Domain;
using FileInviteLite.Api.Services.Audit;
using FluentValidation;

namespace FileInviteLite.Api.Features.Requests;

public sealed class CreateRequest : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/requests", Handle)
           .RequireAuthorization(AuthPolicies.Staff)
           .AddEndpointFilter<ValidationFilter<Body>>()
           .WithName(nameof(CreateRequest));

    public sealed record Body(
        string ClientName, string ClientEmail, DateOnly DueDate, IReadOnlyList<ItemBody> Items);
    public sealed record ItemBody(string Title, string? Description, bool Required);
    public sealed record Response(Guid Id);

    /// <summary>Found by assembly scan and run by <see cref="ValidationFilter{T}"/> (S1.2–S1.4).</summary>
    public sealed class Validator : AbstractValidator<Body>
    {
        private const int MaxItems = 50;

        public Validator(TimeProvider clock)
        {
            RuleFor(b => b.ClientName)
                .NotEmpty().WithMessage("Client name is required.")
                .MaximumLength(200).WithMessage("Client name must be 200 characters or fewer.");

            RuleFor(b => b.ClientEmail)
                .NotEmpty().WithMessage("Client email is required.")
                .Must(IsPlainEmailAddress).WithMessage("Client email must be a valid email address.")
                .MaximumLength(320).WithMessage("Client email must be 320 characters or fewer.");

            // "Today" is the UTC date, the same one the overdue rule uses.
            RuleFor(b => b.DueDate)
                .Must(due => due >= DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime))
                .WithMessage("Due date must be today or later.");

            RuleFor(b => b.Items)
                .Cascade(CascadeMode.Stop)
                .NotEmpty().WithMessage("Items must contain at least one entry.")
                .Must(items => items.Count <= MaxItems)
                .WithMessage($"Items must contain at most {MaxItems} entries.");

            RuleForEach(b => b.Items)
                .Cascade(CascadeMode.Stop)
                .NotNull().WithMessage("Each item must be an object.")
                .ChildRules(item =>
                {
                    item.RuleFor(i => i.Title)
                        .NotEmpty().WithMessage("Title is required.")
                        .MaximumLength(200).WithMessage("Title must be 200 characters or fewer.");

                    item.RuleFor(i => i.Description)
                        .MaximumLength(1000)
                        .WithMessage("Description must be 1000 characters or fewer.");
                });
        }
    }

    /// <summary>
    /// A bare address only. FluentValidation's <c>EmailAddress()</c> accepts anything with an @, so
    /// "jane example@example.com" passes it; <see cref="MailAddress"/> must parse the value back to
    /// itself, which also rules out display names like "Jane &lt;jane@example.com&gt;".
    /// </summary>
    private static bool IsPlainEmailAddress(string? value) =>
        value is not null
        && MailAddress.TryCreate(value, out var parsed)
        && parsed.Address == value.Trim()
        && parsed.Host.Contains('.');

    private static async Task<IResult> Handle(
        Body body,
        AppDbContext db,
        IAuditWriter audit,
        CurrentStaff staff,
        TimeProvider clock,
        CancellationToken ct)
    {
        var request = new DocumentRequest
        {
            ClientName = body.ClientName.Trim(),
            ClientEmail = body.ClientEmail.Trim(),
            DueDate = body.DueDate,
            CreatedBy = staff.Id,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        };

        request.Items.AddRange(body.Items.Select(item => new ChecklistItem
        {
            RequestId = request.Id,
            Title = item.Title.Trim(),
            Description = string.IsNullOrWhiteSpace(item.Description) ? null : item.Description.Trim(),
            Required = item.Required,
        }));

        // Derived, never chosen by hand: with no magic link and nothing uploaded this is Draft.
        request.Status = request.RollUpStatus(magicLinkIssued: false);

        db.DocumentRequests.Add(request);

        audit.Add(AuditAction.RequestCreated, ActorType.Staff, staff.Id,
                  nameof(DocumentRequest), request.Id, new { request.ClientEmail });

        await db.SaveChangesAsync(ct);          // request, items and audit event: one transaction

        return Results.Created($"/api/requests/{request.Id}", new Response(request.Id));
    }
}
