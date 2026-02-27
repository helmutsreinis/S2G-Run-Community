using System;

namespace S2GPulseWeb.Web.Data;

public class UserSecret
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty; // e.g., "OpenAI_ApiKey", "DeepSeek_ApiKey"
    public string Value { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Organization ownership (null = personal secret).
    /// When set, secret is available to all organization workflows.
    /// </summary>
    public Guid? OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public virtual ApplicationUser User { get; set; } = null!;
}
