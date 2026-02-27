using Microsoft.EntityFrameworkCore;
using S2GPulseWeb.Web.Data;
using Stripe;
using Stripe.Checkout;

namespace S2GPulseWeb.Web.Logic;

/// <summary>
/// Service for Stripe API operations
/// </summary>
public class StripeService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly SubscriptionService _subscriptionService;
    private readonly MembershipPlanService _membershipPlanService;
    private readonly WorkflowExecutionService _workflowExecutionService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<StripeService> _logger;
    private readonly bool _isSelfHosted;

    public StripeService(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        SubscriptionService subscriptionService,
        MembershipPlanService membershipPlanService,
        WorkflowExecutionService workflowExecutionService,
        IConfiguration configuration,
        ILogger<StripeService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _subscriptionService = subscriptionService;
        _membershipPlanService = membershipPlanService;
        _workflowExecutionService = workflowExecutionService;
        _configuration = configuration;
        _logger = logger;
        _isSelfHosted = configuration.GetValue<bool>("SelfHosted");
        
        // Configure Stripe with secret key (skip in self-hosted mode)
        if (!_isSelfHosted)
        {
            var secretKey = _configuration["Stripe:SecretKey"];
            if (!string.IsNullOrEmpty(secretKey))
            {
                StripeConfiguration.ApiKey = secretKey;
            }
        }
    }

    /// <summary>
    /// Create a Stripe Checkout session for a membership plan
    /// </summary>
    public async Task<string> CreateCheckoutSessionAsync(string userId, int planId, string successUrl, string cancelUrl)
    {
        // Self-hosted mode: billing is disabled
        if (_isSelfHosted)
            throw new InvalidOperationException("Billing is disabled in self-hosted mode");
        
        // Get the plan from database
        var plan = await _membershipPlanService.GetPlanByIdAsync(planId);
        if (plan == null)
        {
            throw new InvalidOperationException($"Plan {planId} not found");
        }
        
        if (plan.IsFree)
        {
            throw new InvalidOperationException("Cannot checkout for free plan");
        }
        
        if (plan.IsContactSales)
        {
            throw new InvalidOperationException("This plan requires contacting sales");
        }
        
        if (string.IsNullOrEmpty(plan.StripePriceId))
        {
            throw new InvalidOperationException($"Plan {plan.Name} does not have a Stripe Price ID configured");
        }
        
        // Check if plan can accept new members
        var (canAccept, reason) = await _membershipPlanService.CanAcceptNewMembersAsync(planId);
        if (!canAccept)
        {
            throw new InvalidOperationException(reason ?? "Cannot accept new members for this plan");
        }

        // Ensure user has a subscription record
        var subscription = await _subscriptionService.EnsureSubscriptionAsync(userId);
        
        // Get or create Stripe customer
        var stripeCustomerId = await GetOrCreateStripeCustomerAsync(userId);
        
        var options = new SessionCreateOptions
        {
            Customer = stripeCustomerId,
            PaymentMethodTypes = new List<string> { "card" },
            LineItems = new List<SessionLineItemOptions>
            {
                new SessionLineItemOptions
                {
                    Price = plan.StripePriceId,
                    Quantity = 1
                }
            },
            Mode = "subscription",
            SuccessUrl = successUrl + "?session_id={CHECKOUT_SESSION_ID}",
            CancelUrl = cancelUrl,
            Metadata = new Dictionary<string, string>
            {
                ["userId"] = userId,
                ["planId"] = planId.ToString()
            }
        };

        var service = new SessionService();
        var session = await service.CreateAsync(options);
        
        _logger.LogInformation("Created Stripe checkout session {SessionId} for user {UserId}, plan {PlanName}", 
            session.Id, userId, plan.Name);
        
        return session.Url;
    }
    
    /// <summary>
    /// Create a Stripe Checkout session for upgrading to Starter tier (legacy method for backwards compatibility)
    /// </summary>
    [Obsolete("Use CreateCheckoutSessionAsync with planId instead")]
    public async Task<string> CreateCheckoutSessionAsync(string userId, string successUrl, string cancelUrl)
    {
        // Find the Starter plan by name for backwards compatibility
        var plans = await _membershipPlanService.GetAllPlansAsync();
        var starterPlan = plans.FirstOrDefault(p => p.Name == "Starter");
        
        if (starterPlan == null)
        {
            // Fallback to config-based price ID
            var priceId = _configuration["Stripe:StarterPriceId"];
            if (string.IsNullOrEmpty(priceId))
            {
                throw new InvalidOperationException("No Starter plan configured");
            }
            
            // Create session with legacy approach
            var subscription = await _subscriptionService.EnsureSubscriptionAsync(userId);
            var stripeCustomerId = await GetOrCreateStripeCustomerAsync(userId);
            
            var options = new SessionCreateOptions
            {
                Customer = stripeCustomerId,
                PaymentMethodTypes = new List<string> { "card" },
                LineItems = new List<SessionLineItemOptions>
                {
                    new SessionLineItemOptions { Price = priceId, Quantity = 1 }
                },
                Mode = "subscription",
                SuccessUrl = successUrl + "?session_id={CHECKOUT_SESSION_ID}",
                CancelUrl = cancelUrl,
                Metadata = new Dictionary<string, string> { ["userId"] = userId }
            };
            
            var service = new SessionService();
            var session = await service.CreateAsync(options);
            return session.Url;
        }
        
        return await CreateCheckoutSessionAsync(userId, starterPlan.Id, successUrl, cancelUrl);
    }

    /// <summary>
    /// Get or create a Stripe customer for the user
    /// </summary>
    public async Task<string> GetOrCreateStripeCustomerAsync(string userId)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        
        // Check if user already has a Stripe customer ID
        var subscription = await db.UserSubscriptions
            .FirstOrDefaultAsync(s => s.UserId == userId);
            
        if (subscription?.StripeCustomerId != null)
        {
            return subscription.StripeCustomerId;
        }
        
        // Get user email
        var user = await db.Users.FindAsync(userId);
        if (user == null)
        {
            throw new InvalidOperationException($"User {userId} not found");
        }
        
        // Create Stripe customer
        var customerOptions = new CustomerCreateOptions
        {
            Email = user.Email,
            Name = $"{user.FirstName} {user.LastName}".Trim(),
            Metadata = new Dictionary<string, string>
            {
                ["userId"] = userId
            }
        };
        
        var customerService = new CustomerService();
        var customer = await customerService.CreateAsync(customerOptions);
        
        // Save the customer ID
        await _subscriptionService.SetStripeCustomerIdAsync(userId, customer.Id);
        
        _logger.LogInformation("Created Stripe customer {CustomerId} for user {UserId}", 
            customer.Id, userId);
        
        return customer.Id;
    }

    /// <summary>
    /// Create a customer portal session for managing subscription
    /// </summary>
    public async Task<string> CreateCustomerPortalSessionAsync(string userId, string returnUrl)
    {
        var subscription = await _subscriptionService.GetSubscriptionAsync(userId);
        
        if (subscription?.StripeCustomerId == null)
        {
            throw new InvalidOperationException("User does not have a Stripe customer account");
        }
        
        var options = new Stripe.BillingPortal.SessionCreateOptions
        {
            Customer = subscription.StripeCustomerId,
            ReturnUrl = returnUrl
        };
        
        var service = new Stripe.BillingPortal.SessionService();
        var session = await service.CreateAsync(options);
        
        _logger.LogInformation("Created Stripe portal session for user {UserId}", userId);
        
        return session.Url;
    }

    /// <summary>
    /// Handle webhook event from Stripe
    /// </summary>
    public async Task HandleWebhookEventAsync(Event stripeEvent)
    {
        // Self-hosted mode: no webhook processing
        if (_isSelfHosted) return;
        
        _logger.LogInformation("Processing Stripe webhook: {EventType}", stripeEvent.Type);
        
        switch (stripeEvent.Type)
        {
            case "checkout.session.completed":
                await HandleCheckoutSessionCompleted(stripeEvent);
                break;
                
            case "customer.subscription.updated":
                await HandleSubscriptionUpdated(stripeEvent);
                break;
                
            case "customer.subscription.deleted":
                await HandleSubscriptionDeleted(stripeEvent);
                break;
                
            case "invoice.paid":
                _logger.LogInformation("Invoice paid event received");
                break;
                
            case "invoice.payment_failed":
                await HandlePaymentFailed(stripeEvent);
                break;
                
            default:
                _logger.LogDebug("Unhandled Stripe event type: {EventType}", stripeEvent.Type);
                break;
        }
    }

    private async Task HandleCheckoutSessionCompleted(Event stripeEvent)
    {
        var session = stripeEvent.Data.Object as Session;
        if (session == null) return;
        
        var userId = session.Metadata?.GetValueOrDefault("userId");
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("Checkout session {SessionId} has no userId metadata", session.Id);
            return;
        }
        
        // Get subscription details
        if (!string.IsNullOrEmpty(session.SubscriptionId))
        {
            var stripeSubService = new Stripe.SubscriptionService();
            var stripeSubscription = await stripeSubService.GetAsync(session.SubscriptionId);
            
            // Get billing period from first subscription item (Stripe API 2025+)
            var firstItem = stripeSubscription.Items?.Data?.FirstOrDefault();
            
            await _subscriptionService.UpdateSubscriptionFromStripeAsync(
                session.CustomerId,
                stripeSubscription.Id,
                firstItem?.Price?.Id ?? "",
                stripeSubscription.Status,
                firstItem?.CurrentPeriodStart,
                firstItem?.CurrentPeriodEnd,
                stripeSubscription.CancelAtPeriodEnd);
        }
        
        _logger.LogInformation("Checkout completed for user {UserId}", userId);
    }

    private async Task HandleSubscriptionUpdated(Event stripeEvent)
    {
        var stripeSub = stripeEvent.Data.Object as Stripe.Subscription;
        if (stripeSub == null) return;
        
        // Get billing period from first subscription item (Stripe API 2025+)
        var firstItem = stripeSub.Items?.Data?.FirstOrDefault();
        
        // Detect cancellation: Either cancel_at_period_end is true OR cancel_at has a value
        // Stripe uses cancel_at when user schedules cancellation (vs immediate)
        var cancelAtPeriodEnd = stripeSub.CancelAtPeriodEnd;
        var cancelAt = stripeSub.CancelAt; // DateTime? when subscription will be cancelled
        
        // If cancel_at has a value, the subscription is scheduled for cancellation
        var isCanceling = cancelAtPeriodEnd || cancelAt.HasValue;
        
        // Use cancel_at date if set, otherwise use period end
        var effectiveEndDate = cancelAt ?? firstItem?.CurrentPeriodEnd;
        
        _logger.LogInformation(
            "Subscription update: CustomerId={CustomerId}, Status={Status}, CancelAtPeriodEnd={CancelAtPeriodEnd}, CancelAt={CancelAt}, IsCanceling={IsCanceling}", 
            stripeSub.CustomerId, stripeSub.Status, cancelAtPeriodEnd, cancelAt, isCanceling);
        
        await _subscriptionService.UpdateSubscriptionFromStripeAsync(
            stripeSub.CustomerId,
            stripeSub.Id,
            firstItem?.Price?.Id ?? "",
            stripeSub.Status,
            firstItem?.CurrentPeriodStart,
            effectiveEndDate,
            isCanceling);
        
        // Handle upgrade: re-enable orgs if subscription is active
        if (stripeSub.Status == "active" && !isCanceling)
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            var userSub = await db.UserSubscriptions
                .Include(s => s.MembershipPlan)
                .FirstOrDefaultAsync(s => s.StripeCustomerId == stripeSub.CustomerId);
            
            if (userSub?.MembershipPlan != null)
            {
                await _subscriptionService.HandleUpgradeAsync(userSub.UserId, userSub.MembershipPlan);
            }
        }
    }

    private async Task HandleSubscriptionDeleted(Event stripeEvent)
    {
        var stripeSub = stripeEvent.Data.Object as Stripe.Subscription;
        if (stripeSub == null) return;
        
        // Get user ID from Stripe customer before updating subscription
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var userSubscription = await db.UserSubscriptions
            .FirstOrDefaultAsync(s => s.StripeCustomerId == stripeSub.CustomerId);
        
        await _subscriptionService.UpdateSubscriptionFromStripeAsync(
            stripeSub.CustomerId,
            stripeSub.Id,
            "",
            "canceled",
            null,
            null);
        
        // Handle downgrade side-effects
        if (userSubscription != null)
        {
            // Stop all running workflows for this user (downgraded to free tier)
            await _workflowExecutionService.StopAllUserWorkflowsAsync(
                userSubscription.UserId, 
                "Subscription cancelled - downgraded to free tier");
            _logger.LogInformation("Stopped all workflows for user {UserId} due to subscription cancellation", 
                userSubscription.UserId);
            
            // Reset personal usage and disable organizations
            var freePlan = await db.MembershipPlans.FirstOrDefaultAsync(p => p.IsFree && p.IsActive);
            if (freePlan != null)
            {
                await _subscriptionService.HandleDowngradeAsync(userSubscription.UserId, freePlan);
            }
        }
            
        _logger.LogInformation("Subscription {SubscriptionId} canceled", stripeSub.Id);
    }

    private Task HandlePaymentFailed(Event stripeEvent)
    {
        var invoice = stripeEvent.Data.Object as Invoice;
        if (invoice == null) return Task.CompletedTask;
        
        _logger.LogWarning("Payment failed for invoice {InvoiceId}, customer {CustomerId}", 
            invoice.Id, invoice.CustomerId);
        
        // TODO: Could send notification to user or update subscription status to past_due
        return Task.CompletedTask;
    }



    /// <summary>
    /// Verify webhook signature
    /// </summary>
    public Event ConstructEvent(string json, string stripeSignature)
    {
        var webhookSecret = _configuration["Stripe:WebhookSecret"];
        
        if (string.IsNullOrEmpty(webhookSecret))
        {
            _logger.LogWarning("Stripe WebhookSecret not configured - skipping signature verification");
            return EventUtility.ParseEvent(json);
        }
        
        return EventUtility.ConstructEvent(json, stripeSignature, webhookSecret, throwOnApiVersionMismatch: false);
    }
}
