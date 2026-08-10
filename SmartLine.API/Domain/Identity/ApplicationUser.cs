using Microsoft.AspNetCore.Identity;

namespace SmartLine.API.Domain.Identity;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
