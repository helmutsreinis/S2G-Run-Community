using System;
using System.Collections.Generic;

namespace S2GPulseWeb.Web.Data;

public class WorkflowAction
{
    public string Action { get; set; } = string.Empty; // create_node, delete_node, connect_nodes, set_property, clear_workflow
    public Dictionary<string, object> Parameters { get; set; } = new();
}

public class WorkflowAssistantResult
{
    public string Message { get; set; } = string.Empty;
    public List<WorkflowAction> Actions { get; set; } = new();
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

public class ChatMessage
{
    public string Role { get; set; } = string.Empty; // user, assistant
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class ChatConversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "New Conversation";
    public List<ChatMessage> Messages { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastMessageAt { get; set; } = DateTime.UtcNow;
}
