using System;

namespace S2GPulseWeb.Web.Data;

/// <summary>
/// Stores user preferences like last opened workflow
/// </summary>
public class UserPreference
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public Guid? LastWorkflowId { get; set; }
    
    /// <summary>
    /// Designated Copilot OAuth connection for AI Builder.
    /// When set, this connection is used when "Copilot" is selected as the AI provider in workflow designer.
    /// </summary>
    public Guid? AiBuilderCopilotConnectionId { get; set; }
    
    /// <summary>
    /// Currently active organization context for this user.
    /// When null, the user is operating in their personal context.
    /// When set, the user is operating on behalf of the specified organization.
    /// </summary>
    public Guid? ActiveOrganizationId { get; set; }
    
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
