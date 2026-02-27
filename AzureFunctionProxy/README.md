# Azure Function Proxy for S2G Pulse Web

This Azure Function acts as a public-facing HTTP proxy that routes requests to internal S2G Pulse Web listener nodes.

## Features

- **Wildcard subdomain routing**: Route requests based on subdomain (e.g., `abc-123.listener.mydomain.com`)
- **Header-based routing**: Use `X-S2G-Node-Id` header to specify target node
- **Query parameter routing**: Use `?nodeId=xxx` query parameter
- **Full HTTP method support**: GET, POST, PUT, DELETE, PATCH
- **Request/response forwarding**: Preserves headers, body, query parameters
- **Security**: API key authentication between proxy and S2G Web

## Local Development

1. **Prerequisites**:
   - Azure Functions Core Tools: `npm install -g azure-functions-core-tools@4`
   - .NET 9 SDK

2. **Configure settings**:
   Edit `local.settings.json`:
   ```json
   {
     "Values": {
       "S2G_WEB_APP_URL": "https://localhost:5001",
       "S2G_API_KEY": "your-dev-api-key"
     }
   }
   ```

3. **Run locally**:
   ```bash
   cd AzureFunctionProxy
   func start
   ```

4. **Test**:
   ```bash
   # Using header-based routing
   curl http://localhost:7071/api/test \
     -H "X-S2G-Node-Id: your-node-id-here"
   
   # Using query parameter
   curl http://localhost:7071/api/test?nodeId=your-node-id-here
   ```

## Azure Deployment

### Option 1: Using Azure CLI

```bash
# Create Function App
az functionapp create \
  --resource-group s2gpulseweb-rg \
  --name s2gpulseweb-proxy \
  --storage-account s2gpulsewebstorage \
  --runtime dotnet-isolated \
  --functions-version 4 \
  --consumption-plan-location eastus

# Configure app settings
az functionapp config appsettings set \
  --resource-group s2gpulseweb-rg \
  --name s2gpulseweb-proxy \
  --settings \
    S2G_WEB_APP_URL=https://s2gpulseweb-web.azurecontainerapps.io \
    S2G_API_KEY=your-production-api-key

# Deploy function
func azure functionapp publish s2gpulseweb-proxy
```

### Option 2: GitHub Actions

Create `.github/workflows/deploy-azure-function.yml`:

```yaml
name: Deploy Azure Function Proxy

on:
  push:
    branches: [ main ]
    paths:
      - 'AzureFunctionProxy/**'

jobs:
  deploy:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      
      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '9.0.x'
      
      - name: Build project
        run: |
          cd AzureFunctionProxy
          dotnet build --configuration Release
          dotnet publish --configuration Release --output ./output
      
      - name: Deploy to Azure Functions
        uses: Azure/functions-action@v1
        with:
          app-name: s2gpulseweb-proxy
          package: './AzureFunctionProxy/output'
          publish-profile: ${{ secrets.AZURE_FUNCTIONAPP_PUBLISH_PROFILE }}
```

## Custom Domain Configuration

### Configure Wildcard Domain

1. **Add custom domain to Function App**:
   ```bash
   az functionapp config hostname add \
     --resource-group s2gpulseweb-rg \
     --name s2gpulseweb-proxy \
     --hostname "*.listener.mydomain.com"
   ```

2. **Create DNS records**:
   - Add CNAME record: `*.listener.mydomain.com` → `s2gpulseweb-proxy.azurewebsites.net`
   - Or use Azure DNS Zone for automatic management

3. **Configure SSL certificate**:
   ```bash
   # Upload wildcard certificate
   az functionapp config ssl upload \
     --resource-group s2gpulseweb-rg \
     --name s2gpulseweb-proxy \
     --certificate-file wildcard.pfx \
     --certificate-password "password"
   
   # Bind certificate to domain
   az functionapp config ssl bind \
     --resource-group s2gpulseweb-rg \
     --name s2gpulseweb-proxy \
     --certificate-thumbprint "thumbprint" \
     --ssl-type SNI
   ```

## Usage Examples

### Example 1: Subdomain Routing

Create a listener node in S2G Web with ID `abc-123-def-456`.

Request:
```bash
curl https://abc-123-def-456.listener.mydomain.com/webhook \
  -X POST \
  -H "Content-Type: application/json" \
  -d '{"message": "Hello from webhook"}'
```

The Azure Function extracts `abc-123-def-456` from the subdomain and routes to that specific listener node.

### Example 2: Header-Based Routing

Request:
```bash
curl https://listener.mydomain.com/api/process \
  -H "X-S2G-Node-Id: abc-123-def-456" \
  -H "Content-Type: application/json" \
  -d '{"data": "process this"}'
```

### Example 3: Query Parameter Routing

Request:
```bash
curl https://listener.mydomain.com/callback?nodeId=abc-123-def-456&status=complete
```

## Security

### API Key Authentication

The function includes API key validation between the proxy and S2G Web:

1. **Set API key** in Function App settings:
   ```bash
   az functionapp config appsettings set \
     --resource-group s2gpulseweb-rg \
     --name s2gpulseweb-proxy \
     --settings S2G_API_KEY=random-secure-key-here
   ```

2. **Configure same key** in S2G Web app settings

3. The function automatically adds `X-S2G-Api-Key` header to internal requests

### Rate Limiting

Azure Functions includes built-in rate limiting. Configure in `host.json`:

```json
{
  "extensions": {
    "http": {
      "maxConcurrentRequests": 100,
      "maxOutstandingRequests": 200
    }
  }
}
```

## Monitoring

### Application Insights

Enable Application Insights for monitoring:

```bash
az monitor app-insights component create \
  --app s2gpulseweb-proxy-insights \
  --location eastus \
  --resource-group s2gpulseweb-rg

# Link to Function App
az functionapp config appsettings set \
  --resource-group s2gpulseweb-rg \
  --name s2gpulseweb-proxy \
  --settings APPLICATIONINSIGHTS_CONNECTION_STRING="connection-string"
```

### Key Metrics

- **Request count**: Total number of proxied requests
- **Response time**: End-to-end latency
- **Error rate**: Failed proxy attempts
- **Cold start frequency**: Function initialization time

## Troubleshooting

### Common Issues

**Issue**: "Could not extract Node ID"
- **Solution**: Ensure subdomain, header, or query parameter is correctly formatted

**Issue**: "Could not reach S2G Web application"
- **Solution**: Check S2G_WEB_APP_URL is correct and S2G Web is running

**Issue**: High latency
- **Solution**: Use Premium plan instead of Consumption plan to avoid cold starts

### Logs

View logs in real-time:
```bash
func azure functionapp logstream s2gpulseweb-proxy
```

Or using Azure CLI:
```bash
az webapp log tail --name s2gpulseweb-proxy --resource-group s2gpulseweb-rg
```

## Cost Estimation

- **Consumption Plan**: ~$0.20 per million executions
- **Free tier**: 1 million executions/month
- **Typical usage** (10K requests/day): ~300K/month = **Free**

For high-traffic scenarios, consider Premium plan ($150-300/month) to eliminate cold starts.

## Next Steps

1. Deploy Azure Function using instructions above
2. Implement the S2G Web API endpoint (see `docs/azure-function-listener-routing.md`)
3. Update listener nodes to support proxy mode
4. Test end-to-end flow
5. Configure monitoring and alerts

## References

- [Azure Functions Documentation](https://docs.microsoft.com/azure/azure-functions/)
- [Custom Domain Configuration](https://docs.microsoft.com/azure/app-service/app-service-web-tutorial-custom-domain)
- [S2G Listener Routing Architecture](../docs/azure-function-listener-routing.md)
