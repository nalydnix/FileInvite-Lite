namespace FileInviteLite.Api.Domain;

public enum ItemStatus
{
    Outstanding,
    Uploaded,
    Approved,
    Rejected,
}

/// <summary>One document being asked for on a <see cref="DocumentRequest"/>.</summary>
public sealed class ChecklistItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid RequestId { get; init; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public required bool Required { get; set; }
    public ItemStatus Status { get; set; } = ItemStatus.Outstanding;
    public List<UploadedFile> Files { get; init; } = [];
    public List<ReviewDecision> Decisions { get; init; } = [];
}
