using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace S2GPulseWeb.Web.Data;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<WorkflowNode> WorkflowNodes { get; set; } = null!;
    public DbSet<Workflow> Workflows { get; set; } = null!;
    public DbSet<WorkflowConnection> WorkflowConnections { get; set; } = null!;
    public DbSet<UserSecret> UserSecrets { get; set; } = null!;
    public DbSet<NodeLog> NodeLogs { get; set; } = null!;
    public DbSet<LogRetentionSetting> LogRetentionSettings { get; set; } = null!;
    public DbSet<UserPreference> UserPreferences { get; set; } = null!;
    public DbSet<WorkflowExecution> WorkflowExecutions { get; set; } = null!;
    public DbSet<OAuthConnection> OAuthConnections { get; set; } = null!;
    public DbSet<StorageTableColumn> StorageTableColumns { get; set; } = null!;
    public DbSet<StorageTableRecord> StorageTableRecords { get; set; } = null!;
    public DbSet<VectorDocument> VectorDocuments { get; set; } = null!;
    public DbSet<UserSubscription> UserSubscriptions { get; set; } = null!;
    public DbSet<UserUsage> UserUsages { get; set; } = null!;
    public DbSet<LegalDocument> LegalDocuments { get; set; } = null!;
    public DbSet<DeveloperNote> DeveloperNotes { get; set; } = null!;
    public DbSet<UserDismissedNote> UserDismissedNotes { get; set; } = null!;
    public DbSet<ConnectorCategory> ConnectorCategories { get; set; } = null!;
    public DbSet<PlatformConnector> PlatformConnectors { get; set; } = null!;
    
    // Membership Plans
    public DbSet<MembershipPlan> MembershipPlans { get; set; } = null!;
    
    // Organizations
    public DbSet<Organization> Organizations { get; set; } = null!;
    public DbSet<OrganizationMember> OrganizationMembers { get; set; } = null!;
    public DbSet<OrganizationUsage> OrganizationUsages { get; set; } = null!;
    
    // Custom Node Designer entities
    public DbSet<CustomNodeCategory> CustomNodeCategories { get; set; } = null!;
    public DbSet<CustomNodeDefinition> CustomNodeDefinitions { get; set; } = null!;
    public DbSet<CustomNodeInputField> CustomNodeInputFields { get; set; } = null!;
    public DbSet<CustomNodeOutputParameter> CustomNodeOutputParameters { get; set; } = null!;
    public DbSet<CustomNodeConnectionTag> CustomNodeConnectionTags { get; set; } = null!;
    public DbSet<CustomNodeLogConfig> CustomNodeLogConfigs { get; set; } = null!;
    
    // Platform Settings (white-label branding)
    public DbSet<PlatformSetting> PlatformSettings { get; set; } = null!;
    
    // API Keys
    public DbSet<ApiKey> ApiKeys { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<WorkflowNode>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.NodeType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.HasOne(e => e.Workflow)
                .WithMany(w => w.Nodes)
                .HasForeignKey(e => e.WorkflowId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Workflow>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.HasOne(e => e.Owner)
                .WithMany()
                .HasForeignKey(e => e.OwnerId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Organization)
                .WithMany(o => o.Workflows)
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.SetNull);  // Keep workflows if org deleted
            entity.HasIndex(e => e.OrganizationId);
        });

        builder.Entity<WorkflowConnection>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.SourceNode)
                .WithMany(n => n.OutgoingConnections)
                .HasForeignKey(e => e.SourceNodeId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.TargetNode)
                .WithMany(n => n.IncomingConnections)
                .HasForeignKey(e => e.TargetNodeId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<UserSecret>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Organization)
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.SetNull);  // Keep secrets if org deleted
            entity.HasIndex(e => new { e.UserId, e.Name }).IsUnique();
            entity.HasIndex(e => e.OrganizationId);
        });

        builder.Entity<NodeLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired().HasMaxLength(450);
            entity.Property(e => e.NodeName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.NodeType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Message).IsRequired();
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => new { e.UserId, e.Timestamp });
        });

        builder.Entity<LogRetentionSetting>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired().HasMaxLength(450);
            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.UserId).IsUnique();
        });

        builder.Entity<UserPreference>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired().HasMaxLength(450);
            entity.HasIndex(e => e.UserId).IsUnique();
        });

        builder.Entity<WorkflowExecution>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired().HasMaxLength(450);
            entity.HasOne(e => e.Workflow)
                .WithMany()
                .HasForeignKey(e => e.WorkflowId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => new { e.UserId, e.Status });
        });

        builder.Entity<OAuthConnection>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired().HasMaxLength(450);
            entity.Property(e => e.ConnectionName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Provider).IsRequired().HasMaxLength(50);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => new { e.UserId, e.Provider });
            entity.HasOne(e => e.PlatformConnector)
                .WithMany(c => c.Connections)
                .HasForeignKey(e => e.PlatformConnectorId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Organization)
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.SetNull);  // Keep connections if org deleted
            entity.HasIndex(e => e.OrganizationId);
        });

        builder.Entity<ConnectorCategory>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.IconEmoji).HasMaxLength(10);
            entity.HasIndex(e => e.DisplayOrder);
        });

        builder.Entity<PlatformConnector>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.ClientId).IsRequired().HasMaxLength(100);
            entity.Property(e => e.TenantId).HasMaxLength(100);
            entity.HasOne(e => e.Category)
                .WithMany(c => c.Connectors)
                .HasForeignKey(e => e.CategoryId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(e => e.CategoryId);
            entity.HasIndex(e => e.IsEnabled);
        });

        builder.Entity<StorageTableColumn>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ColumnName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.ColumnType).IsRequired().HasMaxLength(50);
            entity.HasIndex(e => e.StorageTableNodeId);
            entity.HasIndex(e => new { e.StorageTableNodeId, e.ColumnName }).IsUnique();
        });

        builder.Entity<StorageTableRecord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired().HasMaxLength(450);
            entity.Property(e => e.DataJson).IsRequired();
            entity.HasIndex(e => e.StorageTableNodeId);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => new { e.StorageTableNodeId, e.Timestamp });
        });

        builder.Entity<VectorDocument>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired().HasMaxLength(450);
            entity.Property(e => e.TextContent).IsRequired();
            entity.Property(e => e.Embedding).IsRequired();
            entity.HasIndex(e => e.VectorDbNodeId);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.CreatedAt);
        });

        builder.Entity<MembershipPlan>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.StripePriceId).HasMaxLength(100);
            entity.HasIndex(e => e.DisplayOrder);
            entity.HasIndex(e => e.IsActive);
            entity.HasIndex(e => e.StripePriceId);
        });

        builder.Entity<UserSubscription>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired().HasMaxLength(450);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50);
            entity.Property(e => e.StripeCustomerId).HasMaxLength(100);
            entity.Property(e => e.StripeSubscriptionId).HasMaxLength(100);
            entity.Property(e => e.StripePriceId).HasMaxLength(100);
            entity.HasOne(e => e.User)
                .WithOne()
                .HasForeignKey<UserSubscription>(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.MembershipPlan)
                .WithMany()
                .HasForeignKey(e => e.MembershipPlanId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(e => e.UserId).IsUnique();
            entity.HasIndex(e => e.StripeCustomerId);
            entity.HasIndex(e => e.StripeSubscriptionId);
            entity.HasIndex(e => e.MembershipPlanId);
        });

        builder.Entity<UserUsage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired().HasMaxLength(450);
            entity.HasIndex(e => e.UserId).IsUnique();
        });

        // Custom Node Designer entities
        builder.Entity<CustomNodeCategory>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.IconEmoji).HasMaxLength(10);
            entity.HasIndex(e => e.DisplayOrder);
        });

        builder.Entity<CustomNodeDefinition>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.NodeTypeKey).IsRequired().HasMaxLength(100);
            entity.Property(e => e.DisplayName).IsRequired().HasMaxLength(200);
            entity.HasOne(e => e.Category)
                .WithMany(c => c.Nodes)
                .HasForeignKey(e => e.CategoryId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(e => e.NodeTypeKey).IsUnique();
            entity.HasIndex(e => e.CategoryId);
            entity.HasIndex(e => e.IsEnabled);
        });

        builder.Entity<CustomNodeInputField>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FieldName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.DisplayLabel).IsRequired().HasMaxLength(200);
            entity.HasOne(e => e.NodeDefinition)
                .WithMany(n => n.InputFields)
                .HasForeignKey(e => e.NodeDefinitionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.NodeDefinitionId);
            entity.HasIndex(e => new { e.NodeDefinitionId, e.FieldName }).IsUnique();
        });

        builder.Entity<CustomNodeOutputParameter>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ParameterName).IsRequired().HasMaxLength(100);
            entity.HasOne(e => e.NodeDefinition)
                .WithMany(n => n.OutputParameters)
                .HasForeignKey(e => e.NodeDefinitionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.NodeDefinitionId);
            entity.HasIndex(e => new { e.NodeDefinitionId, e.ParameterName }).IsUnique();
        });

        builder.Entity<CustomNodeConnectionTag>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TagName).IsRequired().HasMaxLength(50);
            entity.HasOne(e => e.NodeDefinition)
                .WithMany(n => n.ConnectionTags)
                .HasForeignKey(e => e.NodeDefinitionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.NodeDefinitionId);
            entity.HasIndex(e => new { e.NodeDefinitionId, e.TagName }).IsUnique();
        });

        builder.Entity<CustomNodeLogConfig>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TargetName).IsRequired().HasMaxLength(100);
            entity.HasOne(e => e.NodeDefinition)
                .WithMany(n => n.LogConfigs)
                .HasForeignKey(e => e.NodeDefinitionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.NodeDefinitionId);
        });

        // Organization entity
        builder.Entity<Organization>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.FounderId).IsRequired().HasMaxLength(450);
            entity.HasOne(e => e.Founder)
                .WithMany()
                .HasForeignKey(e => e.FounderId)
                .OnDelete(DeleteBehavior.Restrict);  // Prevent cascade - handle manually
            entity.HasIndex(e => e.FounderId);
            entity.HasIndex(e => e.IsActive);
        });

        // OrganizationMember entity
        builder.Entity<OrganizationMember>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired().HasMaxLength(450);
            entity.HasOne(e => e.Organization)
                .WithMany(o => o.Members)
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.OrganizationId, e.UserId }).IsUnique();
            entity.HasIndex(e => e.UserId);  // For finding user's organizations
        });

        // OrganizationUsage entity
        builder.Entity<OrganizationUsage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Organization)
                .WithOne()
                .HasForeignKey<OrganizationUsage>(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.OrganizationId).IsUnique();
        });

        // Platform Settings (white-label branding)
        builder.Entity<PlatformSetting>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Key).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Value).IsRequired();
            entity.HasIndex(e => e.Key).IsUnique();
        });
        
        // API Keys
        builder.Entity<ApiKey>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.KeyHash).IsRequired().HasMaxLength(128);
            entity.HasIndex(e => e.KeyHash).IsUnique();
            entity.Property(e => e.KeyPrefix).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
