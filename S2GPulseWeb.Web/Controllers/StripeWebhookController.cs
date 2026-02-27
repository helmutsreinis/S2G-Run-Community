using Microsoft.AspNetCore.Mvc;
using S2GPulseWeb.Web.Logic;

namespace S2GPulseWeb.Web.Controllers;

/// <summary>
/// Webhook controller for Stripe events
/// </summary>
[Route("api/stripe")]
[ApiController]
public class StripeWebhookController : ControllerBase
{
    private readonly StripeService _stripeService;
    private readonly ILogger<StripeWebhookController> _logger;

    public StripeWebhookController(
        StripeService stripeService,
        ILogger<StripeWebhookController> logger)
    {
        _stripeService = stripeService;
        _logger = logger;
    }

    /// <summary>
    /// Handle Stripe webhook events
    /// </summary>
    [HttpPost("webhook")]
    public async Task<IActionResult> HandleWebhook()
    {
        // Read raw body for signature verification
        string json;
        using (var reader = new StreamReader(HttpContext.Request.Body))
        {
            json = await reader.ReadToEndAsync();
        }

        var stripeSignature = Request.Headers["Stripe-Signature"].FirstOrDefault();
        
        if (string.IsNullOrEmpty(json))
        {
            _logger.LogWarning("Empty webhook body received");
            return BadRequest("Empty request body");
        }

        try
        {
            // Verify signature and parse event
            var stripeEvent = _stripeService.ConstructEvent(json, stripeSignature ?? "");
            
            // Process event asynchronously (return 200 quickly)
            _ = Task.Run(async () =>
            {
                try
                {
                    await _stripeService.HandleWebhookEventAsync(stripeEvent);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing Stripe webhook event {EventId}", stripeEvent.Id);
                }
            });

            // Return 200 immediately to acknowledge receipt
            return Ok(new { received = true, eventId = stripeEvent.Id });
        }
        catch (Stripe.StripeException ex)
        {
            _logger.LogError(ex, "Stripe webhook signature verification failed");
            return BadRequest($"Webhook error: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error processing Stripe webhook");
            return StatusCode(500, "Internal error");
        }
    }
}
