using System.Text.Json;

namespace S2GPulseWeb.Web.Logic;

/// <summary>
/// Utility class for extracting property paths from JSON structures.
/// Used for dynamic placeholder generation based on last execution results.
/// </summary>
public static class JsonPropertyExtractor
{
    /// <summary>
    /// Represents a property with its path and detected type
    /// </summary>
    public class PropertyInfo
    {
        public string Path { get; set; } = "";
        public string Type { get; set; } = "unknown";
        public bool IsArray { get; set; }
    }

    /// <summary>
    /// Extracts all property paths from a JSON string with type annotations.
    /// Returns paths like: "user.name" -> "string", "items" -> "array", "count" -> "number"
    /// </summary>
    /// <param name="json">The JSON string to parse</param>
    /// <param name="maxDepth">Maximum recursion depth (default 5)</param>
    /// <returns>Dictionary of property paths to their detected types</returns>
    public static Dictionary<string, PropertyInfo> ExtractPropertyPaths(string? json, int maxDepth = 5)
    {
        var result = new Dictionary<string, PropertyInfo>();
        
        if (string.IsNullOrWhiteSpace(json))
            return result;

        try
        {
            using var doc = JsonDocument.Parse(json);
            ExtractFromElement(doc.RootElement, "", result, 0, maxDepth);
        }
        catch (JsonException)
        {
            // Invalid JSON, return empty result
        }

        return result;
    }

    private static void ExtractFromElement(
        JsonElement element, 
        string currentPath, 
        Dictionary<string, PropertyInfo> result, 
        int depth, 
        int maxDepth)
    {
        if (depth > maxDepth)
            return;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var propertyPath = string.IsNullOrEmpty(currentPath) 
                        ? property.Name 
                        : $"{currentPath}.{property.Name}";
                    
                    var propType = GetTypeString(property.Value.ValueKind);
                    var isArray = property.Value.ValueKind == JsonValueKind.Array;
                    
                    result[propertyPath] = new PropertyInfo
                    {
                        Path = propertyPath,
                        Type = propType,
                        IsArray = isArray
                    };

                    // Recurse into nested objects and arrays
                    if (property.Value.ValueKind == JsonValueKind.Object)
                    {
                        ExtractFromElement(property.Value, propertyPath, result, depth + 1, maxDepth);
                    }
                    else if (property.Value.ValueKind == JsonValueKind.Array)
                    {
                        ExtractFromArray(property.Value, propertyPath, result, depth + 1, maxDepth);
                    }
                }
                break;

            case JsonValueKind.Array:
                ExtractFromArray(element, currentPath, result, depth, maxDepth);
                break;
        }
    }

    private static void ExtractFromArray(
        JsonElement array, 
        string currentPath, 
        Dictionary<string, PropertyInfo> result, 
        int depth, 
        int maxDepth)
    {
        if (depth > maxDepth)
            return;

        // For arrays, extract schema from the first element (if present)
        if (array.GetArrayLength() > 0)
        {
            var firstElement = array[0];
            
            if (firstElement.ValueKind == JsonValueKind.Object)
            {
                // Add [0] suffix to indicate array element access
                var arrayElementPath = $"{currentPath}[0]";
                result[arrayElementPath] = new PropertyInfo
                {
                    Path = arrayElementPath,
                    Type = "object",
                    IsArray = false
                };
                
                ExtractFromElement(firstElement, arrayElementPath, result, depth + 1, maxDepth);
            }
            else
            {
                // Primitive array - just note the element type
                var elementType = GetTypeString(firstElement.ValueKind);
                result[$"{currentPath}[0]"] = new PropertyInfo
                {
                    Path = $"{currentPath}[0]",
                    Type = elementType,
                    IsArray = false
                };
            }
        }
    }

    private static string GetTypeString(JsonValueKind kind)
    {
        return kind switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True => "boolean",
            JsonValueKind.False => "boolean",
            JsonValueKind.Null => "null",
            JsonValueKind.Array => "array",
            JsonValueKind.Object => "object",
            _ => "unknown"
        };
    }

    /// <summary>
    /// Converts extracted property paths to placeholder format for a given node prefix.
    /// </summary>
    /// <param name="properties">Dictionary of property paths from ExtractPropertyPaths</param>
    /// <param name="nodeName">The node name to use as prefix (e.g., "Listener")</param>
    /// <param name="outputProperty">The output property name (e.g., "Body")</param>
    /// <returns>List of placeholder strings in {{NodeName.Property.Path}} format</returns>
    public static List<string> ToPlaceholders(
        Dictionary<string, PropertyInfo> properties, 
        string nodeName, 
        string outputProperty)
    {
        var placeholders = new List<string>();
        
        foreach (var kvp in properties)
        {
            var path = kvp.Key;
            var info = kvp.Value;
            
            // Skip array marker placeholders that have child properties
            // (e.g., skip "items" if we have "items[0].id")
            if (info.IsArray && properties.Keys.Any(k => k.StartsWith($"{path}[")))
                continue;
                
            var placeholder = $"{{{{{nodeName}.{outputProperty}.{path}}}}}";
            placeholders.Add(placeholder);
        }
        
        return placeholders.OrderBy(p => p).ToList();
    }

    /// <summary>
    /// Generates a descriptive summary of detected properties for display in the UI.
    /// </summary>
    public static List<(string Path, string TypeBadge)> GetPropertySummary(
        Dictionary<string, PropertyInfo> properties)
    {
        return properties
            .OrderBy(p => p.Key)
            .Select(p => (p.Key, GetTypeBadge(p.Value)))
            .ToList();
    }

    private static string GetTypeBadge(PropertyInfo info)
    {
        if (info.IsArray)
            return "📦 array";
        
        return info.Type switch
        {
            "string" => "📝 string",
            "number" => "🔢 number",
            "boolean" => "✓ boolean",
            "object" => "📋 object",
            "null" => "∅ null",
            _ => "? unknown"
        };
    }
}
