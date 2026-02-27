using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

/// <summary>
/// Condition node that evaluates expressions and branches based on True/False result.
/// Supports operators: ==, !=, >, <, >=, <=, contains, startsWith, endsWith, isEmpty, isNotEmpty
/// </summary>
public class ConditionNode : BaseNodeExecutor
{
    public ConditionNode(NodeExecutionManager executionManager) : base(executionManager) { }

    public override string NodeType => "Condition";

    public override List<string> GetOutputParameters() => new() { "ConditionResult", "LeftValue", "RightValue" };

    protected override Task<NodeExecutionResult> InternalExecuteAsync(WorkflowNode node, Dictionary<string, object?> inputData, string userId)
    {
        var config = JsonSerializer.Deserialize<ConditionConfig>(node.Configuration ?? "{}") ?? new();
        
        // Resolve placeholders in left and right values
        var leftValue = ResolvePlaceholders(config.LeftValue ?? "", inputData);
        var rightValue = ResolvePlaceholders(config.RightValue ?? "", inputData);
        var op = config.Operator ?? "==";

        Log(node, NodeLogLevel.Info, $"Evaluating condition: '{leftValue}' {op} '{rightValue}'");

        bool result;
        try
        {
            result = EvaluateCondition(leftValue, op, rightValue);
        }
        catch (Exception ex)
        {
            Log(node, NodeLogLevel.Error, $"Condition evaluation failed: {ex.Message}");
            return Task.FromResult(new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = $"Condition evaluation failed: {ex.Message}"
            });
        }

        var resultStr = result ? "true" : "false";
        Log(node, NodeLogLevel.Info, $"Condition result: {resultStr}", 
            JsonSerializer.Serialize(new { LeftValue = leftValue, Operator = op, RightValue = rightValue, Result = result }, 
                new JsonSerializerOptions { WriteIndented = true }));

        return Task.FromResult(new NodeExecutionResult
        {
            Success = true,
            OutputData = new Dictionary<string, object?>
            {
                { "ConditionResult", result },
                { "LeftValue", leftValue },
                { "RightValue", rightValue }
            }
        });
    }

    private bool EvaluateCondition(string left, string op, string right)
    {
        return op.ToLower() switch
        {
            "==" or "equals" => string.Equals(left, right, StringComparison.OrdinalIgnoreCase),
            "!=" or "notequals" => !string.Equals(left, right, StringComparison.OrdinalIgnoreCase),
            ">" => CompareNumeric(left, right) > 0,
            "<" => CompareNumeric(left, right) < 0,
            ">=" => CompareNumeric(left, right) >= 0,
            "<=" => CompareNumeric(left, right) <= 0,
            "contains" => left.Contains(right, StringComparison.OrdinalIgnoreCase),
            "notcontains" => !left.Contains(right, StringComparison.OrdinalIgnoreCase),
            "startswith" => left.StartsWith(right, StringComparison.OrdinalIgnoreCase),
            "endswith" => left.EndsWith(right, StringComparison.OrdinalIgnoreCase),
            "isempty" => string.IsNullOrWhiteSpace(left),
            "isnotempty" => !string.IsNullOrWhiteSpace(left),
            "matches" => Regex.IsMatch(left, right, RegexOptions.IgnoreCase),
            _ => throw new ArgumentException($"Unsupported operator: {op}")
        };
    }

    private int CompareNumeric(string left, string right)
    {
        if (decimal.TryParse(left, out var leftNum) && decimal.TryParse(right, out var rightNum))
        {
            return leftNum.CompareTo(rightNum);
        }
        // Fall back to string comparison if not numeric
        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private string ResolvePlaceholders(string template, Dictionary<string, object?> data)
    {
        if (string.IsNullOrEmpty(template)) return "";
        
        var result = template;
        
        // Handle {{placeholder}} format
        var placeholderRegex = new Regex(@"\{\{([^}]+)\}\}");
        result = placeholderRegex.Replace(result, match =>
        {
            var key = match.Groups[1].Value;
            
            // Try exact match first
            if (data.TryGetValue(key, out var value) && value != null)
                return value.ToString() ?? "";
            
            // Try without node prefix
            var shortKey = key.Contains('.') ? key.Split('.').Last() : key;
            if (data.TryGetValue(shortKey, out var shortValue) && shortValue != null)
                return shortValue.ToString() ?? "";
            
            // Try to find key in any prefixed format
            foreach (var kvp in data)
            {
                if (kvp.Key.EndsWith("." + key) || kvp.Key.EndsWith("." + shortKey))
                {
                    return kvp.Value?.ToString() ?? "";
                }
            }
            
            return match.Value; // Return original if not found
        });
        
        // Handle {placeholder} format
        foreach (var kvp in data)
        {
            if (kvp.Value != null)
            {
                result = result.Replace($"{{{kvp.Key}}}", kvp.Value.ToString() ?? "");
                
                // Also handle short key
                if (kvp.Key.Contains('.'))
                {
                    var shortKey = kvp.Key.Split('.').Last();
                    result = result.Replace($"{{{shortKey}}}", kvp.Value.ToString() ?? "");
                }
            }
        }
        
        return result;
    }
}

public class ConditionConfig
{
    public string? LeftValue { get; set; }
    public string? Operator { get; set; } = "==";
    public string? RightValue { get; set; }
}
