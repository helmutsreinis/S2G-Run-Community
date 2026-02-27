# S2G Run — API Reference

> **Base URL:** `http://localhost:5000` (or your deployed instance)
>
> **Version:** v1

---

## Authentication

Most API endpoints require authentication via **API Key**. Pass your key in the request header:

```
X-API-Key: your-api-key
```

Generate an API key from **Settings → API Keys** in the S2G Run dashboard.

**Exceptions:** The Listener Proxy, OpenClaw Trigger, Remote Client, and Stripe Webhook endpoints use alternative authentication or none (documented per-endpoint).

---

## Table of Contents

| Section | Base Route | Auth |
|---------|-----------|------|
| [Workflows](#workflows) | `/api/v1/workflows` | API Key |
| [Node Catalog](#node-catalog) | `/api/v1/catalog` | API Key |
| [AI Assistant](#ai-assistant) | `/api/v1/ai` | API Key |
| [Connections](#connections) | `/api/v1/connections` | API Key |
| [Knowledge Base](#knowledge-base) | `/api/v1/knowledge` | API Key |
| [Node Logs](#node-logs) | `/api/v1/workflows/{wf}/nodes/{n}` | API Key |
| [Usage & Quotas](#usage--quotas) | `/api/v1/usage` | API Key |
| [Listener Proxy](#listener-proxy) | `/api/listener` | Internal |
| [OpenClaw Trigger](#openclaw-trigger) | `/api/openclaw` | Secret Header |
| [Remote Client](#remote-client) | `/api/remote-client` | None |
| [Stripe Webhook](#stripe-webhook) | `/api/stripe` | Stripe Signature |

---

## Workflows

CRUD operations for workflows, plus start/stop and node/connection management.

### List Workflows

```
GET /api/v1/workflows
```

Returns all workflows for the authenticated user's active context (personal or organization).

**Response** `200 OK`
```json
[
  {
    "id": "guid",
    "name": "My Workflow",
    "description": "...",
    "isActive": false,
    "createdAt": "2026-01-15T10:30:00Z",
    "updatedAt": "2026-01-15T12:00:00Z"
  }
]
```

---

### Get Workflow

```
GET /api/v1/workflows/{id}
```

Returns a specific workflow with full node and connection details.

| Parameter | Type | Location | Description |
|-----------|------|----------|-------------|
| `id` | guid | path | Workflow ID |

**Response** `200 OK`
```json
{
  "id": "guid",
  "name": "My Workflow",
  "description": "...",
  "isActive": false,
  "nodes": [
    {
      "id": "guid",
      "name": "HTTP Listener",
      "nodeType": "HttpListener",
      "x": 100, "y": 200,
      "width": 240, "height": 120,
      "configuration": "{...}"
    }
  ],
  "connections": [
    {
      "id": "guid",
      "sourceNodeId": "guid",
      "targetNodeId": "guid",
      "label": "Success"
    }
  ]
}
```

**Error** `404`
```json
{ "error": "Workflow not found." }
```

---

### Create Workflow

```
POST /api/v1/workflows
```

Creates a new workflow with optional nodes and connections. Auto-layout and auto-labeling are applied.

**Request Body**
```json
{
  "name": "API Endpoint",
  "description": "Handles incoming requests",
  "nodes": [
    {
      "name": "Listener",
      "nodeType": "HttpListener",
      "configuration": "{\"Port\": 8080}"
    },
    {
      "name": "Transform",
      "nodeType": "JavaScript"
    }
  ],
  "connections": [
    {
      "sourceName": "Listener",
      "targetName": "Transform",
      "label": "on-request"
    }
  ]
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `name` | string | ✅ | Workflow name |
| `description` | string | | Optional description |
| `nodes` | array | | Nodes to create |
| `nodes[].name` | string | | Display name |
| `nodes[].nodeType` | string | ✅ | Node type (see Catalog API) |
| `nodes[].configuration` | string | | JSON configuration |
| `nodes[].x`, `y` | number | | Canvas position (auto-laid out if omitted) |
| `connections` | array | | Connections between nodes |
| `connections[].sourceName` | string | ✅ | Source node name |
| `connections[].targetName` | string | ✅ | Target node name |
| `connections[].label` | string | | Connection label |

**Response** `201 Created` — Returns the full workflow object.

**Error** `400` — Unknown node type(s).

---

### Update Workflow

```
PUT /api/v1/workflows/{id}
```

Updates an existing workflow's name, description, nodes, and connections.

**Request Body** — Same structure as Create Workflow.

**Response** `200 OK` — Updated workflow object.

---

### Delete Workflow

```
DELETE /api/v1/workflows/{id}
```

Deletes a workflow and all associated nodes, connections, and logs.

**Response** `200 OK`
```json
{ "success": true }
```

---

### Start Workflow

```
POST /api/v1/workflows/{id}/start
```

Starts a workflow (sets `IsActive=true` for auto-restart durability).

**Response** `200 OK`
```json
{ "message": "Workflow started.", "isActive": true }
```

**Error** `400` — Workflow cannot be started (e.g., quota exceeded).

---

### Stop Workflow

```
POST /api/v1/workflows/{id}/stop
```

Stops a running workflow.

**Response** `200 OK`
```json
{ "message": "Workflow stopped.", "isActive": false }
```

---

### Add Node

```
POST /api/v1/workflows/{id}/nodes
```

Adds a single node to an existing workflow.

**Request Body**
```json
{
  "name": "My JavaScript Node",
  "nodeType": "JavaScript",
  "configuration": "{\"Code\": \"output.set('result', 'hello');\"}"
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `name` | string | ✅ | Node display name |
| `nodeType` | string | ✅ | Type from the catalog |
| `configuration` | string | | JSON config |
| `x`, `y` | number | | Canvas position |

**Response** `201 Created` — Returns the created node.

---

### Remove Node

```
DELETE /api/v1/workflows/{id}/nodes/{nodeId}
```

Removes a node and all its connections from a workflow.

**Response** `200 OK`
```json
{ "message": "Node deleted." }
```

---

### Add Connection

```
POST /api/v1/workflows/{id}/connections
```

Adds a connection between two existing nodes.

**Request Body**
```json
{
  "sourceNodeId": "guid",
  "targetNodeId": "guid",
  "label": "Success",
  "sourceTag": "on-success",
  "targetTag": "input"
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `sourceNodeId` | guid | ✅ | Source node ID |
| `targetNodeId` | guid | ✅ | Target node ID |
| `label` | string | | Display label |
| `sourceTag` | string | | Output tag for routing |
| `targetTag` | string | | Input tag |

**Response** `201 Created`

---

### Remove Connection

```
DELETE /api/v1/workflows/{id}/connections/{connectionId}
```

**Response** `200 OK`
```json
{ "message": "Connection deleted." }
```

---

## Node Catalog

Browse available node types (built-in + custom), their schemas, and connection tags.

### List Categories

```
GET /api/v1/catalog/categories
```

Returns all node categories with node counts.

**Response** `200 OK`
```json
[
  { "name": "Flow Control", "nodeCount": 5 },
  { "name": "Data Processing", "nodeCount": 8 },
  { "name": "AI & LLM", "nodeCount": 4 }
]
```

---

### List All Nodes

```
GET /api/v1/catalog/nodes
```

Returns all available nodes (built-in + custom) with descriptions and connection tags.

**Response** `200 OK`
```json
[
  {
    "type": "JavaScript",
    "name": "JavaScript",
    "description": "Execute custom JavaScript code with full access to input/output.",
    "category": "Data Processing",
    "isCustom": false,
    "connectionTags": {
      "input": ["input"],
      "output": ["on-complete", "on-error"]
    }
  }
]
```

---

### List Nodes by Category

```
GET /api/v1/catalog/categories/{category}/nodes
```

| Parameter | Type | Location | Description |
|-----------|------|----------|-------------|
| `category` | string | path | Category name (URL-encoded) |

**Response** `200 OK` — Array of nodes in the category.

---

### Get Node Schema

```
GET /api/v1/catalog/nodes/{type}/schema
```

Returns the full schema for a node type, including all input fields, output parameters, and connection tags.

| Parameter | Type | Location | Description |
|-----------|------|----------|-------------|
| `type` | string | path | Node type name |

**Response** `200 OK`
```json
{
  "type": "JavaScript",
  "name": "JavaScript",
  "description": "...",
  "category": "Data Processing",
  "isCustom": false,
  "inputs": [
    {
      "name": "Code",
      "type": "code",
      "required": true,
      "description": "JavaScript code to execute",
      "defaultValue": ""
    }
  ],
  "outputs": [
    { "name": "Result", "description": "Script execution result" }
  ],
  "connectionTags": {
    "input": ["input"],
    "output": ["on-complete", "on-error"]
  }
}
```

---

### Get Connection Tags

```
GET /api/v1/catalog/connection-tags
```

Returns the connection tags for all built-in node types (used for routing and auto-labeling).

**Response** `200 OK`
```json
[
  {
    "nodeType": "Conditional",
    "inputTags": ["input"],
    "outputTags": ["on-true", "on-false"]
  }
]
```

---

## AI Assistant

AI-powered workflow generation and sample browsing.

### List Providers

```
GET /api/v1/ai/providers
```

Returns all available AI providers with their models and configuration status.

**Response** `200 OK`
```json
[
  {
    "provider": "OpenAI",
    "models": ["gpt-4o", "gpt-4o-mini", "o3-mini"],
    "defaultModel": "gpt-4o",
    "isConfigured": true,
    "authType": "api_key"
  },
  {
    "provider": "Copilot",
    "models": ["gpt-4o", "claude-3.5-sonnet"],
    "defaultModel": "gpt-4o",
    "isConfigured": false,
    "authType": "oauth"
  }
]
```

---

### Generate Workflow

```
POST /api/v1/ai/generate
```

Generates a workflow from a natural language prompt using AI. The workflow is persisted automatically.

**Request Body**
```json
{
  "prompt": "Create an API endpoint that receives JSON, validates it, and stores to database",
  "name": "JSON Validator API",
  "provider": "OpenAI",
  "model": "gpt-4o",
  "temperature": "Focused"
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `prompt` | string | ✅ | Natural language description |
| `name` | string | | Workflow name (auto-generated from prompt if omitted) |
| `provider` | string | | AI provider: `OpenAI`, `Anthropic`, `Copilot`, `Gemini` (default: `OpenAI`) |
| `model` | string | | Model name (provider-specific, uses default if omitted) |
| `temperature` | string | | `Focused`, `Balanced`, or `Creative` (default: `Focused`) |

**Response** `201 Created`
```json
{
  "message": "Created workflow with 4 nodes.",
  "workflow": { "id": "guid", "name": "...", "nodes": [...], "connections": [...] },
  "success": true
}
```

---

### List Samples

```
GET /api/v1/ai/samples
```

Returns available workflow sample templates.

**Response** `200 OK`
```json
[
  {
    "fileName": "simple-api-endpoint.json",
    "name": "Simple API Endpoint",
    "nodeCount": 3,
    "connectionCount": 2
  }
]
```

---

### Get Sample

```
GET /api/v1/ai/samples/{name}
```

Returns the full JSON of a specific workflow sample.

| Parameter | Type | Location | Description |
|-----------|------|----------|-------------|
| `name` | string | path | Sample filename (with or without `.json`) |

**Response** `200 OK` — Raw JSON workflow definition.

---

## Connections

Manage OAuth connections for external service integrations (Microsoft Graph, Google, GitHub, etc.).

### List Connections

```
GET /api/v1/connections
```

Returns all OAuth connections for the authenticated user's active context. Tokens are **never** exposed.

**Response** `200 OK`
```json
[
  {
    "id": "guid",
    "provider": "MicrosoftGraph",
    "email": "user@example.com",
    "connectionName": "Work M365",
    "createdAt": "2026-01-10T08:00:00Z",
    "lastUsedAt": "2026-01-15T12:00:00Z",
    "organizationId": null,
    "hasPlatformConnector": true
  }
]
```

---

### Get Connection

```
GET /api/v1/connections/{id}
```

Returns details for a specific connection (no tokens exposed).

---

### Create Connection

```
POST /api/v1/connections
```

Creates a new OAuth connection with raw tokens (for programmatic integrations).

**Request Body**
```json
{
  "provider": "MicrosoftGraph",
  "connectionName": "My Graph Connection",
  "accessToken": "eyJ...",
  "refreshToken": "0.ARQA...",
  "tokenExpiry": "2026-02-01T00:00:00Z",
  "scopes": "User.Read Mail.Read",
  "tenantId": "guid",
  "email": "user@example.com"
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `provider` | string | ✅ | Provider name |
| `connectionName` | string | ✅ | Display name |
| `accessToken` | string | ✅ | OAuth access token |
| `refreshToken` | string | | OAuth refresh token |
| `tokenExpiry` | datetime | | Token expiration |
| `scopes` | string | | Granted scopes |
| `tenantId` | string | | Azure AD tenant ID |
| `email` | string | | Associated email |

**Response** `201 Created`

---

### Update Connection

```
PUT /api/v1/connections/{id}
```

Updates a connection's name and/or tokens.

**Request Body**
```json
{
  "connectionName": "Updated Name",
  "accessToken": "new-token",
  "refreshToken": "new-refresh",
  "tokenExpiry": "2026-03-01T00:00:00Z",
  "email": "new@example.com"
}
```

All fields are optional — only provided fields are updated.

---

### Delete Connection

```
DELETE /api/v1/connections/{id}
```

**Response** `200 OK`
```json
{ "message": "Connection deleted." }
```

---

## Knowledge Base

Entity, relation, and graph operations for the built-in knowledge graph. Requires an Azure Storage connection configured in Settings.

### List Entities

```
GET /api/v1/knowledge/entities
```

| Parameter | Type | Location | Description |
|-----------|------|----------|-------------|
| `type` | string | query | Filter by entity type |
| `tag` | string | query | Filter by tag |
| `search` | string | query | Full-text search (in-memory) |
| `limit` | int | query | Page size (1–500, default: 50) |
| `cursor` | string | query | Pagination cursor |

**Response** `200 OK`
```json
{
  "data": [
    {
      "id": "entity-id",
      "title": "My Entity",
      "entityType": "Note",
      "summary": "...",
      "tags": ["tag1", "tag2"],
      "createdAt": "2026-01-10T08:00:00Z",
      "updatedAt": "2026-01-15T12:00:00Z"
    }
  ],
  "pagination": {
    "limit": 50,
    "nextCursor": "cursor-string-or-null"
  }
}
```

> **Note:** When using `search`, results are returned as a flat array (no pagination object) because in-memory filtering has no stable page boundary.

---

### Get Entity

```
GET /api/v1/knowledge/entities/{id}
```

Returns a single entity including its full content.

---

### Create Entity

```
POST /api/v1/knowledge/entities
```

**Request Body**
```json
{
  "title": "Server Architecture",
  "content": "Detailed markdown content...",
  "entityType": "Document",
  "tags": ["architecture", "backend"],
  "properties": { "version": "2.0", "status": "draft" }
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `title` | string | ✅ | Entity title |
| `content` | string | | Full content (markdown) |
| `entityType` | string | | Type (default: `Note`) |
| `tags` | string[] | | Tags for filtering |
| `properties` | object | | Arbitrary key-value metadata |

**Response** `201 Created`

---

### Update Entity

```
PUT /api/v1/knowledge/entities/{id}
```

**Request Body** — Same structure as Create. Only `title` is required; omitted fields retain existing values.

**Response** `200 OK`

---

### Delete Entity

```
DELETE /api/v1/knowledge/entities/{id}
```

Deletes the entity and all its relations.

**Response** `200 OK`
```json
{ "message": "Entity 'entity-id' deleted." }
```

---

### List Relations

```
GET /api/v1/knowledge/entities/{id}/relations
```

| Parameter | Type | Location | Description |
|-----------|------|----------|-------------|
| `id` | string | path | Entity ID |
| `direction` | string | query | `both`, `incoming`, or `outgoing` (default: `both`) |
| `limit` | int | query | Page size (1–500, default: 50) |
| `cursor` | string | query | Pagination cursor (outgoing only) |

**Response** `200 OK`
```json
{
  "data": [
    {
      "sourceId": "entity-a",
      "targetId": "entity-b",
      "relationType": "depends-on"
    }
  ],
  "pagination": { "limit": 50, "nextCursor": null }
}
```

---

### Create Relation

```
POST /api/v1/knowledge/relations
```

**Request Body**
```json
{
  "sourceId": "entity-a",
  "targetId": "entity-b",
  "relationType": "depends-on",
  "bidirectional": false
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `sourceId` | string | ✅ | Source entity ID |
| `targetId` | string | ✅ | Target entity ID |
| `relationType` | string | ✅ | Relation label |
| `bidirectional` | bool | | Create reverse relation too (default: `false`) |

---

### Delete Relation

```
DELETE /api/v1/knowledge/relations
```

**Request Body**
```json
{
  "sourceId": "entity-a",
  "targetId": "entity-b",
  "relationType": "depends-on"
}
```

---

### Get Graph

```
GET /api/v1/knowledge/graph
```

Returns the full knowledge graph for visualization.

| Parameter | Type | Location | Description |
|-----------|------|----------|-------------|
| `type` | string | query | Filter by entity type |
| `tag` | string | query | Filter by tag |
| `maxNodes` | int | query | Maximum nodes to return (default: 200) |

**Response** `200 OK` — Graph data with nodes and edges suitable for rendering.

---

## Node Logs

Read execution logs and manage logging settings for individual nodes.

### Get Logs

```
GET /api/v1/workflows/{workflowId}/nodes/{nodeId}/logs
```

Returns paginated execution logs for a specific node.

| Parameter | Type | Location | Description |
|-----------|------|----------|-------------|
| `workflowId` | guid | path | Workflow ID |
| `nodeId` | guid | path | Node ID |
| `level` | string | query | Filter: `Info`, `Warning`, `Error`, `Debug` |
| `dateFrom` | datetime | query | Start date filter |
| `dateTo` | datetime | query | End date filter |
| `search` | string | query | Text search in message |
| `page` | int | query | Page number (default: 1) |
| `pageSize` | int | query | Items per page (default: 50) |

**Response** `200 OK`
```json
{
  "totalCount": 142,
  "page": 1,
  "pageSize": 50,
  "totalPages": 3,
  "logs": [
    {
      "id": "guid",
      "nodeName": "My JavaScript",
      "nodeType": "JavaScript",
      "timestamp": "2026-01-15T12:00:00Z",
      "level": "Info",
      "message": "Script executed successfully",
      "detail": "Output: { result: 'hello' }"
    }
  ]
}
```

---

### Get Logging Settings

```
GET /api/v1/workflows/{workflowId}/nodes/{nodeId}/logging-settings
```

**Response** `200 OK`
```json
{
  "nodeId": "guid",
  "nodeName": "My JavaScript",
  "settings": "{\"LoggingEnabled\":true,\"LogInfo\":true,\"LogWarning\":true,\"LogError\":true,\"LogDebug\":false}"
}
```

---

### Update Logging Settings

```
PUT /api/v1/workflows/{workflowId}/nodes/{nodeId}/logging-settings
```

**Request Body**
```json
{
  "settingsJson": "{\"LoggingEnabled\":true,\"LogInfo\":true,\"LogWarning\":true,\"LogError\":true,\"LogDebug\":true}"
}
```

---

## Usage & Quotas

Check current resource consumption against plan limits.

### Get Usage

```
GET /api/v1/usage
```

**Response** `200 OK`
```json
{
  "executions": {
    "used": 1250,
    "limit": 10000,
    "percent": 12.5
  },
  "storage": {
    "usedBytes": 52428800,
    "limitBytes": 1073741824,
    "percent": 4.9
  },
  "vectorDocs": {
    "used": 45,
    "limit": 500,
    "percent": 9.0
  },
  "workflows": {
    "used": 8,
    "limit": 50,
    "percent": 16.0
  }
}
```

---

## Listener Proxy

Internal endpoint for Azure Function Proxy to route HTTP requests to listener nodes. Not intended for direct external use.

### Proxy Request

```
POST /api/listener/proxy
```

**Auth:** Internal (Azure Function shared secret via `X-S2G-Api-Key` header)

**Request Body**
```json
{
  "nodeId": "guid-string",
  "method": "POST",
  "path": "/webhook",
  "queryString": "key=value",
  "headers": { "Content-Type": "application/json" },
  "body": "{\"event\": \"order.created\"}"
}
```

**Response** `200 OK`
```json
{
  "statusCode": 200,
  "body": "OK",
  "contentType": "application/json",
  "headers": {}
}
```

---

### Health Check

```
GET /api/listener/health
```

**Auth:** None

**Response** `200 OK`
```json
{ "status": "healthy", "timestamp": "2026-01-15T12:00:00Z" }
```

---

## OpenClaw Trigger

Direct inbound trigger endpoint for [OpenClaw](https://openclaw.ai) AI agent gateway integration.

### Trigger Workflow

```
POST /api/openclaw/trigger/{nodeId}
```

**Auth:** Optional secret via `x-openclaw-secret` header or `Authorization: Bearer <secret>`.

**Request Body**
```json
{
  "prompt": "What is the status of order #1234?",
  "session_key": "user-session-abc",
  "data": {
    "customer_id": "C-001",
    "priority": "high"
  }
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `prompt` | string | | The user's message / AI prompt |
| `session_key` | string | | Session identifier for conversation continuity |
| `data` | object | | Arbitrary extra key-value pairs injected as workflow input |

**Response** `200 OK`
```json
{
  "response": "Order #1234 is currently being shipped.",
  "outputData": {
    "AIResponse": "Order #1234 is currently being shipped.",
    "orderId": "1234",
    "status": "shipping"
  },
  "timedOut": false
}
```

---

### Health Check

```
GET /api/openclaw/health
```

**Auth:** None

**Response** `200 OK`
```json
{
  "status": "healthy",
  "service": "S2G OpenClaw Trigger",
  "timestamp": "2026-01-15T12:00:00Z"
}
```

---

## Remote Client

Download pre-configured remote client scripts for on-premise agent deployment. No authentication required — scripts contain only node routing IDs, not secrets.

### Download Python Client

```
GET /api/remote-client/python?listenerId={id}&clientId={id}
```

| Parameter | Type | Location | Description |
|-----------|------|----------|-------------|
| `listenerId` | string | query | Remote listener node ID |
| `clientId` | string | query | Remote client node ID |

**Response** `200 OK` — Downloads `remote_client.py` with pre-configured variables.

---

### Download PowerShell Client

```
GET /api/remote-client/powershell?listenerId={id}&clientId={id}
```

Same parameters as Python. Returns `RemoteClient.ps1` with UTF-8 BOM for PowerShell 5.x compatibility.

---

## Stripe Webhook

Handles Stripe payment events for subscription management. Not for external use.

### Handle Webhook

```
POST /api/stripe/webhook
```

**Auth:** Stripe signature verification via `Stripe-Signature` header.

**Response** `200 OK`
```json
{ "received": true, "eventId": "evt_..." }
```

---

## Error Format

All endpoints return errors in a consistent format:

```json
{ "error": "Human-readable error message." }
```

Common HTTP status codes:

| Code | Meaning |
|------|---------|
| `200` | Success |
| `201` | Created (returned by POST that creates a resource) |
| `400` | Bad request (validation error, missing fields) |
| `401` | Unauthorized (missing or invalid API key / secret) |
| `404` | Resource not found |
| `500` | Internal server error |

---

## Rate Limits

Rate limits depend on your membership plan. Check current usage via `GET /api/v1/usage`. When limits are exceeded, workflow start operations return `400` with an error message.

---

<div align="center">

**[S2G Run](https://s2g.run)** — Just Run It

*PolyForm Noncommercial License 1.0.0*

</div>
