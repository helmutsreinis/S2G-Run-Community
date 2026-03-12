namespace S2GPulseWeb.Web.Logic;

public enum TutorialPlacement
{
    Top,
    Bottom,
    Left,
    Right
}

public record TutorialStep(
    int StepNumber,
    string TargetSelector,
    string Title,
    string Description,
    TutorialPlacement Placement,
    string? ActionHint = null,
    bool RequiresInteraction = false
);

/// <summary>
/// Defines the onboarding tutorial steps shown to new users.
/// </summary>
public static class TutorialStepDefinition
{
    public static readonly TutorialStep[] Steps =
    [
        new(0, "", "Welcome to S2G Pulse! 👋",
            "This quick tour will show you how to build your first workflow. You can skip at any time.",
            TutorialPlacement.Bottom),

        new(1, "[data-tutorial='workflow-selector']", "Your Workflows",
            "All your workflows appear here. Switch between them using this dropdown.",
            TutorialPlacement.Bottom),

        new(2, "[data-tutorial='new-workflow']", "Create a Workflow",
            "Click here to create a new blank workflow canvas.",
            TutorialPlacement.Bottom,
            "👆 Click the ✨ New button to continue",
            RequiresInteraction: true),

        new(3, "[data-tutorial='workflow-name']", "Name Your Workflow",
            "Give your workflow a meaningful name. Changes are tracked automatically.",
            TutorialPlacement.Bottom),

        new(4, "[data-tutorial='catalog-panel']", "Node Catalog",
            "Open the catalog to browse all available nodes — triggers, actions, logic, and integrations.",
            TutorialPlacement.Left,
            "👆 Click the catalog panel to open it",
            RequiresInteraction: true),

        new(5, "[data-tutorial='catalog-panel']", "Find & Add Nodes",
            "Search for nodes by name, or expand categories. Drag any node onto the canvas to add it.",
            TutorialPlacement.Left),

        new(6, "[data-tutorial='canvas']", "The Workflow Canvas",
            "This is your workspace. Drag nodes to position them, scroll to zoom, and right-click for options.",
            TutorialPlacement.Top),

        new(7, "[data-tutorial='canvas']", "Node Connections",
            "Click a node's output port (right side) and drag to another node's input port to connect them. Connections control the flow of data.",
            TutorialPlacement.Top),

        new(8, "[data-tutorial='start-button']", "Run Your Workflow",
            "When ready, click Start to execute your workflow. The canvas will animate node execution in real-time.",
            TutorialPlacement.Bottom),

        new(9, "[data-tutorial='save-button']", "Save Your Work",
            "Remember to save! Look for the ⚠️ unsaved changes indicator in the header.",
            TutorialPlacement.Bottom),

        new(10, "[data-tutorial='ai-builder']", "AI Assistant",
            "Need help? Open the AI Builder to describe what you want in plain English — it can build nodes for you.",
            TutorialPlacement.Right),

        new(11, "[data-tutorial='nav-settings']", "Settings & API Keys",
            "Head to Settings to configure AI API keys and preferences. You can restart this tutorial there anytime. That's it — happy building! 🚀",
            TutorialPlacement.Bottom),
    ];

    public static int TotalSteps => Steps.Length;
}
