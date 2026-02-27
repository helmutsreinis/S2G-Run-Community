using System;
using System.Collections.Generic;

namespace S2GPulseWeb.Web.Data;

public class KbEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string EntityType { get; set; } = "Note";
    public string? Content { get; set; }
    public List<string> Tags { get; set; } = new();
    public Dictionary<string, object> Properties { get; set; } = new();
    public string? Summary { get; set; }
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class KbRelation
{
    public string SourceId { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public string? SourceTitle { get; set; }
    public string? TargetTitle { get; set; }
    public string RelationType { get; set; } = "related_to";
    public bool Bidirectional { get; set; }
    public Dictionary<string, object>? Properties { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}

public class KbSearchResult
{
    public string EntityId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public List<string> Tags { get; set; } = new();
    public double Score { get; set; }
    public string? MatchedField { get; set; }
}

public class KbGraph
{
    public List<KbGraphNode> Nodes { get; set; } = new();
    public List<KbGraphEdge> Edges { get; set; } = new();
}

public class KbGraphNode
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public int ConnectionCount { get; set; }
    public List<string> Tags { get; set; } = new();
}

public class KbGraphEdge
{
    public string Source { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public bool Bidirectional { get; set; }
}

public class KbSubgraph
{
    public List<KbEntity> Nodes { get; set; } = new();
    public List<KbRelation> Edges { get; set; } = new();
}

/// <summary>
/// Configuration for the Knowledge workflow node.
/// </summary>
public class KnowledgeNodeConfig
{
    // Operation selector
    public string? Operation { get; set; }

    // Entity fields (all support {{placeholder}} resolution)
    public string? EntityId { get; set; }
    public string? Title { get; set; }
    public string? Content { get; set; }
    public string? EntityType { get; set; }
    public string? Tags { get; set; }           // Comma-separated
    public string? Properties { get; set; }     // JSON string

    // Relation fields
    public string? SourceId { get; set; }
    public string? TargetId { get; set; }
    public string? RelationType { get; set; }
    public bool Bidirectional { get; set; }

    // Query/list fields
    public string? Query { get; set; }
    public int MaxResults { get; set; } = 20;
    public int Depth { get; set; } = 2;
    public string? Direction { get; set; }      // "incoming", "outgoing", "both"
    public int MaxNodes { get; set; } = 200;
}
