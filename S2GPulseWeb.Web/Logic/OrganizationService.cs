using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic;

/// <summary>
/// Core service for organization management including CRUD, membership, and workflow transfer operations.
/// </summary>
public class OrganizationService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly WorkflowService _workflowService;
    private readonly CacheStorageService _cacheStorageService;
    private readonly IConfiguration _configuration;

    public OrganizationService(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        WorkflowService workflowService,
        CacheStorageService cacheStorageService,
        IConfiguration configuration)
    {
        _dbContextFactory = dbContextFactory;
        _workflowService = workflowService;
        _cacheStorageService = cacheStorageService;
        _configuration = configuration;
    }

    #region Organization CRUD

    /// <summary>
    /// Get all organizations the user is a member of.
    /// </summary>
    public async Task<List<OrganizationListItem>> GetUserOrganizationsAsync(string userId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        return await context.OrganizationMembers
            .Where(m => m.UserId == userId && m.Organization.IsActive)
            .Select(m => new OrganizationListItem
            {
                Id = m.Organization.Id,
                Name = m.Organization.Name,
                Description = m.Organization.Description,
                SvgLogo = m.Organization.SvgLogo,
                Role = m.Role,
                MemberCount = m.Organization.Members.Count,
                WorkflowCount = m.Organization.Workflows.Count,
                CreatedAt = m.Organization.CreatedAt,
                IsDisabled = m.Organization.IsDisabled,
                DisabledAt = m.Organization.DisabledAt
            })
            .OrderBy(o => o.Name)
            .ToListAsync();
    }

    /// <summary>
    /// Get organization details if user has access.
    /// </summary>
    public async Task<Organization?> GetOrganizationAsync(Guid orgId, string userId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        // Verify user is a member
        var membership = await context.OrganizationMembers
            .FirstOrDefaultAsync(m => m.OrganizationId == orgId && m.UserId == userId);
        
        if (membership == null) return null;
        
        return await context.Organizations
            .Include(o => o.Founder)
            .Include(o => o.Members)
            .ThenInclude(m => m.User)
            .FirstOrDefaultAsync(o => o.Id == orgId && o.IsActive);
    }

    /// <summary>
    /// Create a new organization. Caller becomes the Founder.
    /// </summary>
    public async Task<OrganizationResult> CreateOrganizationAsync(string userId, string name, string? description, string? svgLogo)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        // Check if user can create organizations (quota check)
        var canCreate = await CanCreateOrganizationAsync(userId);
        if (!canCreate.Allowed)
        {
            return new OrganizationResult { Success = false, Error = canCreate.Reason };
        }
        
        // Create organization
        var org = new Organization
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            Description = description?.Trim(),
            SvgLogo = svgLogo,
            FounderId = userId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsActive = true
        };
        
        context.Organizations.Add(org);
        
        // Add founder as member with Founder role
        var founderMembership = new OrganizationMember
        {
            OrganizationId = org.Id,
            UserId = userId,
            Role = OrganizationRole.Founder,
            JoinedAt = DateTime.UtcNow
        };
        
        context.OrganizationMembers.Add(founderMembership);
        
        // Create usage tracking record
        var usage = new OrganizationUsage
        {
            OrganizationId = org.Id,
            PeriodStart = DateTime.UtcNow,
            LastUpdated = DateTime.UtcNow
        };
        
        context.OrganizationUsages.Add(usage);
        
        await context.SaveChangesAsync();
        
        return new OrganizationResult { Success = true, OrganizationId = org.Id };
    }

    /// <summary>
    /// Update organization details. Requires Owner or Founder role.
    /// </summary>
    public async Task<OrganizationResult> UpdateOrganizationAsync(Guid orgId, string userId, string name, string? description, string? svgLogo)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        var role = await GetUserRoleInternalAsync(context, orgId, userId);
        if (role == null || role < OrganizationRole.Owner)
        {
            return new OrganizationResult { Success = false, Error = "You don't have permission to update this organization" };
        }
        
        var org = await context.Organizations.FindAsync(orgId);
        if (org == null || !org.IsActive)
        {
            return new OrganizationResult { Success = false, Error = "Organization not found" };
        }
        
        org.Name = name.Trim();
        org.Description = description?.Trim();
        org.SvgLogo = svgLogo;
        org.UpdatedAt = DateTime.UtcNow;
        
        await context.SaveChangesAsync();
        
        return new OrganizationResult { Success = true, OrganizationId = orgId };
    }

    /// <summary>
    /// Check if user can create another organization based on their plan quota.
    /// </summary>
    public async Task<(bool Allowed, string? Reason)> CanCreateOrganizationAsync(string userId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        // Get user's membership plan
        var subscription = await context.UserSubscriptions
            .Include(s => s.MembershipPlan)
            .FirstOrDefaultAsync(s => s.UserId == userId);
        
        var plan = subscription?.MembershipPlan;
        if (plan == null || plan.MaxOrganizations == 0)
        {
            return (false, "Your current plan does not include organization features. Please upgrade to create organizations.");
        }
        
        // -1 means unlimited
        if (plan.MaxOrganizations < 0) return (true, null);
        
        // Count existing organizations where user is founder
        var existingCount = await context.Organizations
            .CountAsync(o => o.FounderId == userId && o.IsActive);
        
        if (existingCount >= plan.MaxOrganizations)
        {
            return (false, $"You have reached the maximum of {plan.MaxOrganizations} organization(s) for your plan.");
        }
        
        return (true, null);
    }

    /// <summary>
    /// Get user's currently active organization from preferences.
    /// </summary>
    public async Task<Guid?> GetActiveOrganizationIdAsync(string userId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        var pref = await context.UserPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId);
        
        if (pref?.ActiveOrganizationId == null) return null;
        
        var orgId = pref.ActiveOrganizationId.Value;
        
        // Verify user is still a member of this organization
        var isMember = await context.OrganizationMembers
            .AnyAsync(m => m.OrganizationId == orgId && m.UserId == userId);
        
        if (!isMember)
        {
            // Clear invalid org reference
            pref.ActiveOrganizationId = null;
            await context.SaveChangesAsync();
            return null;
        }
        
        // Check if org is disabled - block access for ALL members
        var org = await context.Organizations.FindAsync(orgId);
        if (org is { IsDisabled: true })
        {
            pref.ActiveOrganizationId = null;
            pref.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
            return null;
        }
        
        return orgId;
    }

    /// <summary>
    /// Set user's active organization context.
    /// </summary>
    public async Task<bool> SetActiveOrganizationAsync(string userId, Guid? orgId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        // If setting to an org, verify membership and not disabled
        if (orgId.HasValue)
        {
            var isMember = await context.OrganizationMembers
                .AnyAsync(m => m.OrganizationId == orgId && m.UserId == userId);
            
            if (!isMember) return false;
            
            // Block switching to disabled organizations
            var org = await context.Organizations.FindAsync(orgId.Value);
            if (org is { IsDisabled: true })
                return false;
        }
        
        var pref = await context.UserPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId);
        
        if (pref == null)
        {
            pref = new UserPreference
            {
                UserId = userId,
                ActiveOrganizationId = orgId
            };
            context.UserPreferences.Add(pref);
        }
        else
        {
            pref.ActiveOrganizationId = orgId;
            pref.UpdatedAt = DateTime.UtcNow;
        }
        
        await context.SaveChangesAsync();
        return true;
    }

    #endregion

    #region Member Management

    /// <summary>
    /// Get user's role in an organization.
    /// </summary>
    public async Task<OrganizationRole?> GetUserRoleAsync(Guid orgId, string userId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await GetUserRoleInternalAsync(context, orgId, userId);
    }

    private static async Task<OrganizationRole?> GetUserRoleInternalAsync(ApplicationDbContext context, Guid orgId, string userId)
    {
        var membership = await context.OrganizationMembers
            .FirstOrDefaultAsync(m => m.OrganizationId == orgId && m.UserId == userId);
        
        return membership?.Role;
    }

    /// <summary>
    /// Get all members of an organization.
    /// </summary>
    public async Task<List<OrganizationMemberInfo>> GetMembersAsync(Guid orgId, string requestingUserId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        // Verify requester is a member
        var requesterRole = await GetUserRoleInternalAsync(context, orgId, requestingUserId);
        if (requesterRole == null) return new List<OrganizationMemberInfo>();
        
        return await context.OrganizationMembers
            .Where(m => m.OrganizationId == orgId)
            .Select(m => new OrganizationMemberInfo
            {
                UserId = m.UserId,
                Email = m.User.Email ?? "",
                FirstName = m.User.FirstName,
                LastName = m.User.LastName,
                Role = m.Role,
                JoinedAt = m.JoinedAt,
                InvitedByUserId = m.InvitedByUserId
            })
            .OrderByDescending(m => m.Role)
            .ThenBy(m => m.Email)
            .ToListAsync();
    }

    /// <summary>
    /// Search registered users by email for inviting to organization.
    /// Only returns users not already members of the specified organization.
    /// NOTE: This method is deprecated and no longer exposed in the UI to prevent user enumeration.
    /// Use AddMemberAsync with exact email instead.
    /// </summary>
    [Obsolete("User search is no longer exposed in UI to prevent user enumeration. Use AddMemberAsync with exact email.")]
    public async Task<List<UserSearchResult>> SearchUsersAsync(Guid orgId, string emailQuery)
    {
        if (string.IsNullOrWhiteSpace(emailQuery) || emailQuery.Length < 3)
            return new List<UserSearchResult>();
            
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        // Get existing member user IDs
        var existingMemberIds = await context.OrganizationMembers
            .Where(m => m.OrganizationId == orgId)
            .Select(m => m.UserId)
            .ToListAsync();
        
        // Search users by email, excluding existing members
        return await context.Users
            .Where(u => u.Email != null && 
                       u.Email.ToLower().Contains(emailQuery.ToLower()) &&
                       !existingMemberIds.Contains(u.Id))
            .Take(10)
            .Select(u => new UserSearchResult
            {
                UserId = u.Id,
                Email = u.Email ?? "",
                FirstName = u.FirstName,
                LastName = u.LastName
            })
            .ToListAsync();
    }

    /// <summary>
    /// Add a registered user to the organization.
    /// Requires Owner or Founder role to add members.
    /// </summary>
    public async Task<OrganizationResult> AddMemberAsync(Guid orgId, string inviterUserId, string memberEmail, OrganizationRole role)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        // Verify inviter has permission
        var inviterRole = await GetUserRoleInternalAsync(context, orgId, inviterUserId);
        if (inviterRole == null || inviterRole < OrganizationRole.Owner)
        {
            return new OrganizationResult { Success = false, Error = "You don't have permission to add members" };
        }
        
        // Cannot assign Founder role
        if (role == OrganizationRole.Founder)
        {
            return new OrganizationResult { Success = false, Error = "Cannot assign Founder role" };
        }
        
        // Find user by email
        var user = await context.Users
            .FirstOrDefaultAsync(u => u.Email != null && u.Email.ToLower() == memberEmail.ToLower());
        
        if (user == null)
        {
            return new OrganizationResult { Success = false, Error = "User not found. Only registered users can be added to organizations." };
        }
        
        // Check if already a member
        var existingMembership = await context.OrganizationMembers
            .FirstOrDefaultAsync(m => m.OrganizationId == orgId && m.UserId == user.Id);
        
        if (existingMembership != null)
        {
            return new OrganizationResult { Success = false, Error = "User is already a member of this organization" };
        }
        
        // Check member limit
        var canAdd = await CanAddMemberAsync(orgId);
        if (!canAdd.Allowed)
        {
            return new OrganizationResult { Success = false, Error = canAdd.Reason };
        }
        
        // Add member
        var membership = new OrganizationMember
        {
            OrganizationId = orgId,
            UserId = user.Id,
            Role = role,
            JoinedAt = DateTime.UtcNow,
            InvitedAt = DateTime.UtcNow,
            InvitedByUserId = inviterUserId
        };
        
        context.OrganizationMembers.Add(membership);
        await context.SaveChangesAsync();
        
        return new OrganizationResult { Success = true };
    }

    /// <summary>
    /// Remove a member from the organization.
    /// Founders cannot be removed. Owners can remove contributors.
    /// </summary>
    public async Task<OrganizationResult> RemoveMemberAsync(Guid orgId, string removerUserId, string memberUserId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        // Get remover's role
        var removerRole = await GetUserRoleInternalAsync(context, orgId, removerUserId);
        if (removerRole == null || removerRole < OrganizationRole.Owner)
        {
            return new OrganizationResult { Success = false, Error = "You don't have permission to remove members" };
        }
        
        // Get target membership
        var membership = await context.OrganizationMembers
            .FirstOrDefaultAsync(m => m.OrganizationId == orgId && m.UserId == memberUserId);
        
        if (membership == null)
        {
            return new OrganizationResult { Success = false, Error = "Member not found" };
        }
        
        // Cannot remove founder
        if (membership.Role == OrganizationRole.Founder)
        {
            return new OrganizationResult { Success = false, Error = "Founders cannot be removed from the organization" };
        }
        
        // Non-founders can only remove lower roles
        if (removerRole != OrganizationRole.Founder && membership.Role >= removerRole)
        {
            return new OrganizationResult { Success = false, Error = "You can only remove members with lower roles than yours" };
        }
        
        context.OrganizationMembers.Remove(membership);
        await context.SaveChangesAsync();
        
        return new OrganizationResult { Success = true };
    }

    /// <summary>
    /// Update a member's role. Owners can promote/demote contributors.
    /// </summary>
    public async Task<OrganizationResult> UpdateMemberRoleAsync(Guid orgId, string updaterUserId, string memberUserId, OrganizationRole newRole)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        // Cannot assign Founder role
        if (newRole == OrganizationRole.Founder)
        {
            return new OrganizationResult { Success = false, Error = "Cannot assign Founder role" };
        }
        
        // Get updater's role
        var updaterRole = await GetUserRoleInternalAsync(context, orgId, updaterUserId);
        if (updaterRole == null || updaterRole < OrganizationRole.Owner)
        {
            return new OrganizationResult { Success = false, Error = "You don't have permission to change roles" };
        }
        
        // Get target membership
        var membership = await context.OrganizationMembers
            .FirstOrDefaultAsync(m => m.OrganizationId == orgId && m.UserId == memberUserId);
        
        if (membership == null)
        {
            return new OrganizationResult { Success = false, Error = "Member not found" };
        }
        
        // Cannot change founder's role
        if (membership.Role == OrganizationRole.Founder)
        {
            return new OrganizationResult { Success = false, Error = "Founder's role cannot be changed" };
        }
        
        membership.Role = newRole;
        await context.SaveChangesAsync();
        
        return new OrganizationResult { Success = true };
    }

    /// <summary>
    /// Check if organization can accept more members based on plan limits.
    /// </summary>
    public async Task<(bool Allowed, string? Reason)> CanAddMemberAsync(Guid orgId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        var org = await context.Organizations
            .FirstOrDefaultAsync(o => o.Id == orgId);
        
        if (org == null) return (false, "Organization not found");
        
        // Get founder's plan
        var subscription = await context.UserSubscriptions
            .Include(s => s.MembershipPlan)
            .FirstOrDefaultAsync(s => s.UserId == org.FounderId);
        
        var plan = subscription?.MembershipPlan;
        if (plan == null) return (false, "Plan not found");
        
        // -1 or 0 means unlimited
        if (plan.MaxMembersPerOrganization <= 0) return (true, null);
        
        var currentCount = await context.OrganizationMembers
            .CountAsync(m => m.OrganizationId == orgId);
        
        if (currentCount >= plan.MaxMembersPerOrganization)
        {
            return (false, $"Organization has reached the maximum of {plan.MaxMembersPerOrganization} members for your plan.");
        }
        
        return (true, null);
    }

    #endregion

    #region Workflow Transfer

    /// <summary>
    /// Transfer a personal workflow to an organization.
    /// User must own the workflow and be a member of the organization.
    /// </summary>
    public async Task<OrganizationResult> TransferWorkflowToOrganizationAsync(Guid workflowId, Guid orgId, string userId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        // Verify user is member of org
        var role = await GetUserRoleInternalAsync(context, orgId, userId);
        if (role == null)
        {
            return new OrganizationResult { Success = false, Error = "You are not a member of this organization" };
        }
        
        // Get workflow
        var workflow = await context.Workflows
            .FirstOrDefaultAsync(w => w.Id == workflowId && w.OwnerId == userId);
        
        if (workflow == null)
        {
            return new OrganizationResult { Success = false, Error = "Workflow not found or you don't own it" };
        }
        
        if (workflow.OrganizationId != null)
        {
            return new OrganizationResult { Success = false, Error = "Workflow is already assigned to an organization" };
        }
        
        // Check org workflow limit
        var canAdd = await CanAddWorkflowToOrgAsync(orgId);
        if (!canAdd.Allowed)
        {
            return new OrganizationResult { Success = false, Error = canAdd.Reason };
        }
        
        workflow.OrganizationId = orgId;
        workflow.UpdatedAt = DateTime.UtcNow;
        
        await context.SaveChangesAsync();
        
        return new OrganizationResult { Success = true };
    }

    /// <summary>
    /// Transfer an organization workflow back to personal space.
    /// User must be the original owner (OwnerId) of the workflow.
    /// </summary>
    public async Task<OrganizationResult> TransferWorkflowToPersonalAsync(Guid workflowId, string userId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        // Get workflow - user must be original owner
        var workflow = await context.Workflows
            .FirstOrDefaultAsync(w => w.Id == workflowId && w.OwnerId == userId);
        
        if (workflow == null)
        {
            return new OrganizationResult { Success = false, Error = "Workflow not found or you are not the original owner" };
        }
        
        if (workflow.OrganizationId == null)
        {
            return new OrganizationResult { Success = false, Error = "Workflow is already in personal space" };
        }
        
        // Check user's personal workflow quota
        var subscription = await context.UserSubscriptions
            .Include(s => s.MembershipPlan)
            .FirstOrDefaultAsync(s => s.UserId == userId);
        
        var plan = subscription?.MembershipPlan;
        if (plan != null && plan.MaxWorkflows > 0)
        {
            var personalWorkflowCount = await context.Workflows
                .CountAsync(w => w.OwnerId == userId && w.OrganizationId == null);
            
            if (personalWorkflowCount >= plan.MaxWorkflows)
            {
                return new OrganizationResult { Success = false, Error = $"You have reached your personal workflow limit of {plan.MaxWorkflows}" };
            }
        }
        
        workflow.OrganizationId = null;
        workflow.UpdatedAt = DateTime.UtcNow;
        
        await context.SaveChangesAsync();
        
        return new OrganizationResult { Success = true };
    }

    private async Task<(bool Allowed, string? Reason)> CanAddWorkflowToOrgAsync(Guid orgId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        var org = await context.Organizations
            .FirstOrDefaultAsync(o => o.Id == orgId);
        
        if (org == null) return (false, "Organization not found");
        
        // Get founder's plan
        var subscription = await context.UserSubscriptions
            .Include(s => s.MembershipPlan)
            .FirstOrDefaultAsync(s => s.UserId == org.FounderId);
        
        var plan = subscription?.MembershipPlan;
        if (plan == null) return (false, "Plan not found");
        
        // -1 or 0 means unlimited
        if (plan.MaxWorkflowsPerOrganization <= 0) return (true, null);
        
        var currentCount = await context.Workflows
            .CountAsync(w => w.OrganizationId == orgId);
        
        if (currentCount >= plan.MaxWorkflowsPerOrganization)
        {
            return (false, $"Organization has reached the maximum of {plan.MaxWorkflowsPerOrganization} workflows.");
        }
        
        return (true, null);
    }

    #endregion

    #region Organization Deletion

    /// <summary>
    /// Delete organization with specified action for workflows.
    /// Only Founder can delete.
    /// </summary>
    public async Task<OrganizationDeletionResult> DeleteOrganizationAsync(
        Guid orgId, 
        string userId, 
        OrganizationDeletionAction action)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        // Verify caller is Founder
        var role = await GetUserRoleInternalAsync(context, orgId, userId);
        if (role != OrganizationRole.Founder)
        {
            return new OrganizationDeletionResult { Success = false, Error = "Only the Founder can delete an organization" };
        }
        
        var org = await context.Organizations
            .Include(o => o.Members)
            .Include(o => o.Workflows)
            .FirstOrDefaultAsync(o => o.Id == orgId);
        
        if (org == null)
        {
            return new OrganizationDeletionResult { Success = false, Error = "Organization not found" };
        }
        
        var result = new OrganizationDeletionResult { Success = true };
        
        // Handle workflows based on action
        switch (action)
        {
            case OrganizationDeletionAction.TransferToFounder:
                var transferResult = await TransferAllWorkflowsToFounderAsync(context, org, userId);
                if (!transferResult.Success)
                {
                    return new OrganizationDeletionResult { Success = false, Error = transferResult.Error };
                }
                result.WorkflowsTransferred = org.Workflows.Count;
                break;
                
            case OrganizationDeletionAction.ExportAndDelete:
            case OrganizationDeletionAction.DeleteAll:
                // Delete all org workflows and their data
                await DeleteAllOrganizationWorkflowsAsync(context, org);
                result.WorkflowsDeleted = org.Workflows.Count;
                break;
        }
        
        // Delete Azure Blob Storage container
        await DeleteOrganizationStorageContainerAsync(orgId);
        
        // Delete database records
        await DeleteOrganizationDatabaseRecordsAsync(context, org);
        
        return result;
    }

    private async Task<OrganizationResult> TransferAllWorkflowsToFounderAsync(ApplicationDbContext context, Organization org, string founderId)
    {
        // Check founder's personal quota
        var subscription = await context.UserSubscriptions
            .Include(s => s.MembershipPlan)
            .FirstOrDefaultAsync(s => s.UserId == founderId);
        
        var plan = subscription?.MembershipPlan;
        if (plan != null && plan.MaxWorkflows > 0)
        {
            var personalCount = await context.Workflows
                .CountAsync(w => w.OwnerId == founderId && w.OrganizationId == null);
            
            var orgWorkflowCount = org.Workflows.Count;
            if (personalCount + orgWorkflowCount > plan.MaxWorkflows)
            {
                return new OrganizationResult 
                { 
                    Success = false, 
                    Error = $"Cannot transfer {orgWorkflowCount} workflows. You currently have {personalCount}/{plan.MaxWorkflows} personal workflows." 
                };
            }
        }
        
        // Transfer all workflows to personal
        foreach (var workflow in org.Workflows)
        {
            workflow.OrganizationId = null;
            workflow.UpdatedAt = DateTime.UtcNow;
        }
        
        await context.SaveChangesAsync();
        return new OrganizationResult { Success = true };
    }

    private async Task DeleteAllOrganizationWorkflowsAsync(ApplicationDbContext context, Organization org)
    {
        var workflowIds = org.Workflows.Select(w => w.Id).ToList();
        
        foreach (var workflowId in workflowIds)
        {
            // Use workflow service to properly delete each workflow with all associated data
            var workflow = org.Workflows.First(w => w.Id == workflowId);
            await _workflowService.DeleteWorkflowAsync(workflowId, workflow.OwnerId);
        }
    }

    private async Task DeleteOrganizationStorageContainerAsync(Guid orgId)
    {
        try
        {
            var connectionString = _configuration["S2GStorage:ConnectionString"];
            if (string.IsNullOrEmpty(connectionString)) return;
            
            var containerName = $"org-{orgId.ToString().ToLowerInvariant().Replace("-", "")}";
            var blobServiceClient = new BlobServiceClient(connectionString);
            var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
            
            await containerClient.DeleteIfExistsAsync();
        }
        catch
        {
            // Log but don't fail deletion if blob cleanup fails
        }
    }

    private async Task DeleteOrganizationDatabaseRecordsAsync(ApplicationDbContext context, Organization org)
    {
        // Delete organization usage
        var usage = await context.OrganizationUsages
            .FirstOrDefaultAsync(u => u.OrganizationId == org.Id);
        if (usage != null)
            context.OrganizationUsages.Remove(usage);
        
        // Delete organization members (cascade should handle this, but be explicit)
        context.OrganizationMembers.RemoveRange(org.Members);
        
        // Delete organization
        context.Organizations.Remove(org);
        
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Check if workflows can be transferred to founder's personal space.
    /// </summary>
    public async Task<(bool Allowed, string? Reason, int WorkflowCount, int CurrentPersonal, int MaxPersonal)> 
        CanTransferWorkflowsToFounderAsync(Guid orgId, string founderId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        var org = await context.Organizations
            .Include(o => o.Workflows)
            .FirstOrDefaultAsync(o => o.Id == orgId);
        
        if (org == null)
            return (false, "Organization not found", 0, 0, 0);
        
        var subscription = await context.UserSubscriptions
            .Include(s => s.MembershipPlan)
            .FirstOrDefaultAsync(s => s.UserId == founderId);
        
        var plan = subscription?.MembershipPlan;
        var maxWorkflows = plan?.MaxWorkflows ?? 0;
        
        var personalCount = await context.Workflows
            .CountAsync(w => w.OwnerId == founderId && w.OrganizationId == null);
        
        var orgWorkflowCount = org.Workflows.Count;
        
        if (maxWorkflows > 0 && personalCount + orgWorkflowCount > maxWorkflows)
        {
            return (false, $"Transfer would exceed your personal workflow limit of {maxWorkflows}", 
                    orgWorkflowCount, personalCount, maxWorkflows);
        }
        
        return (true, null, orgWorkflowCount, personalCount, maxWorkflows);
    }

    /// <summary>
    /// Export all organization workflows as JSON for download before deletion.
    /// </summary>
    public async Task<List<WorkflowExportData>> ExportOrganizationWorkflowsAsync(Guid orgId, string userId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        // Verify user is founder
        var role = await GetUserRoleInternalAsync(context, orgId, userId);
        if (role != OrganizationRole.Founder)
            return new List<WorkflowExportData>();
        
        return await context.Workflows
            .Where(w => w.OrganizationId == orgId)
            .Include(w => w.Nodes)
            .ThenInclude(n => n.OutgoingConnections)
            .Select(w => new WorkflowExportData
            {
                Id = w.Id,
                Name = w.Name,
                Description = w.Description,
                CreatedAt = w.CreatedAt,
                NodesJson = w.Nodes.Select(n => new { n.Id, n.NodeType, n.Name, n.Configuration, n.PositionX, n.PositionY }).ToList(),
                ConnectionsJson = w.Nodes.SelectMany(n => n.OutgoingConnections).Select(c => new { c.SourceNodeId, c.TargetNodeId, c.Label }).ToList()
            })
            .ToListAsync();
    }

    #endregion

    #region Admin Management

    /// <summary>
    /// Get all organizations for admin management view.
    /// </summary>
    public async Task<List<AdminOrganizationItem>> GetAllOrganizationsForAdminAsync()
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        return await context.Organizations
            .Where(o => o.IsActive)
            .Select(o => new AdminOrganizationItem
            {
                Id = o.Id,
                Name = o.Name,
                Description = o.Description,
                FounderId = o.FounderId,
                FounderEmail = o.Founder != null ? o.Founder.Email ?? "" : "",
                MemberCount = o.Members.Count,
                WorkflowCount = o.Workflows.Count,
                IsDisabled = o.IsDisabled,
                DisabledAt = o.DisabledAt,
                CreatedAt = o.CreatedAt,
                UpdatedAt = o.UpdatedAt
            })
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync();
    }

    /// <summary>
    /// Admin update: set name, disabled state, and DisabledAt timestamp.
    /// </summary>
    public async Task<bool> AdminUpdateOrganizationAsync(Guid orgId, string name, bool isDisabled, DateTime? disabledAt)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        var org = await context.Organizations.FindAsync(orgId);
        if (org == null || !org.IsActive) return false;
        
        org.Name = name.Trim();
        org.IsDisabled = isDisabled;
        
        if (isDisabled)
        {
            var ts = disabledAt ?? org.DisabledAt ?? DateTime.UtcNow;
            org.DisabledAt = ts.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(ts, DateTimeKind.Utc) : ts.ToUniversalTime();
        }
        else
        {
            org.DisabledAt = null;
        }
        
        org.UpdatedAt = DateTime.UtcNow;
        
        // If disabling, clear active org for all members
        if (isDisabled)
        {
            var affectedPrefs = await context.UserPreferences
                .Where(p => p.ActiveOrganizationId == orgId)
                .ToListAsync();
            
            foreach (var pref in affectedPrefs)
            {
                pref.ActiveOrganizationId = null;
                pref.UpdatedAt = DateTime.UtcNow;
            }
        }
        
        await context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Admin delete: permanently remove an organization and all associated data.
    /// </summary>
    public async Task<OrganizationDeletionResult> AdminDeleteOrganizationAsync(Guid orgId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        var org = await context.Organizations
            .Include(o => o.Members)
            .Include(o => o.Workflows)
            .FirstOrDefaultAsync(o => o.Id == orgId);
        
        if (org == null)
            return new OrganizationDeletionResult { Success = false, Error = "Organization not found" };
        
        // Try to transfer workflows to founder first
        var transferResult = await TransferAllWorkflowsToFounderAsync(context, org, org.FounderId);
        var result = new OrganizationDeletionResult { Success = true };
        
        if (transferResult.Success)
        {
            result.WorkflowsTransferred = org.Workflows.Count;
        }
        else
        {
            // If transfer fails, delete all workflows
            await DeleteAllOrganizationWorkflowsAsync(context, org);
            result.WorkflowsDeleted = org.Workflows.Count;
        }
        
        await DeleteOrganizationStorageContainerAsync(orgId);
        await DeleteOrganizationDatabaseRecordsAsync(context, org);
        
        return result;
    }

    #endregion
}

#region DTOs

public enum OrganizationDeletionAction
{
    ExportAndDelete,    // Export all workflows as JSON, then delete
    DeleteAll,          // Delete organization and all workflows
    TransferToFounder   // Transfer workflows to founder's personal space
}

public class OrganizationResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public Guid? OrganizationId { get; set; }
}

public class OrganizationDeletionResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int WorkflowsDeleted { get; set; }
    public int WorkflowsTransferred { get; set; }
}

public class OrganizationListItem
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? SvgLogo { get; set; }
    public OrganizationRole Role { get; set; }
    public int MemberCount { get; set; }
    public int WorkflowCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsDisabled { get; set; }
    public DateTime? DisabledAt { get; set; }
}

public class OrganizationMemberInfo
{
    public string UserId { get; set; } = "";
    public string Email { get; set; } = "";
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public OrganizationRole Role { get; set; }
    public DateTime JoinedAt { get; set; }
    public string? InvitedByUserId { get; set; }
    
    public string DisplayName => !string.IsNullOrEmpty(FirstName) || !string.IsNullOrEmpty(LastName)
        ? $"{FirstName} {LastName}".Trim()
        : Email;
}

public class UserSearchResult
{
    public string UserId { get; set; } = "";
    public string Email { get; set; } = "";
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    
    public string DisplayName => !string.IsNullOrEmpty(FirstName) || !string.IsNullOrEmpty(LastName)
        ? $"{FirstName} {LastName}".Trim()
        : Email;
}

public class WorkflowExportData
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public object? NodesJson { get; set; }
    public object? ConnectionsJson { get; set; }
}

public class AdminOrganizationItem
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string FounderId { get; set; } = "";
    public string FounderEmail { get; set; } = "";
    public int MemberCount { get; set; }
    public int WorkflowCount { get; set; }
    public bool IsDisabled { get; set; }
    public DateTime? DisabledAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    
    public int? GraceDaysRemaining => IsDisabled && DisabledAt.HasValue 
        ? Math.Max(0, 30 - (int)(DateTime.UtcNow - DisabledAt.Value).TotalDays)
        : null;
}

#endregion
