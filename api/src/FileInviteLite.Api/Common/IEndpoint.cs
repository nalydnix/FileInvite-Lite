namespace FileInviteLite.Api.Common;

/// <summary>
/// One endpoint, in one file, mapping itself. Implementations are discovered by reflection at
/// startup (<see cref="EndpointRegistration"/>), so adding an endpoint never edits Program.cs
/// and two parallel feature tasks never collide in a shared registration file. See ADR 0002.
/// </summary>
public interface IEndpoint
{
    void Map(IEndpointRouteBuilder app);
}
