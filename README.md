<div align="center">

# ⚡ S2G Run — Community Edition

### Self-Hosted Workflow Automation Platform

**Build, execute, and monitor powerful workflows — on your own infrastructure.**

[![License: PolyForm Noncommercial](https://img.shields.io/badge/license-PolyForm%20NC%201.0-orange.svg)](https://polyformproject.org/licenses/noncommercial/1.0.0)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-purple.svg)](https://dotnet.microsoft.com)
[![Docker](https://img.shields.io/badge/Docker-Ready-2496ED.svg)](https://docker.com)
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-16-336791.svg)](https://postgresql.org)

---

[Quickstart](#-quickstart) • [Features](#-features) • [Setup Guide](#-detailed-setup-guide) • [OpenClaw AI](#-openclaw--ai-agent-integration) • [Custom Nodes](#-custom-nodes) • [Architecture](#-architecture) • [Contributing](#-contributing)

</div>

---

## 🚀 Quickstart

Get S2G Pulse running in **under 5 minutes** with Docker:

```bash
# 1. Clone the repository
git clone https://github.com/helmutsreinis/S2G-Run-Community.git
cd S2G-Run-Community

# 2. Create your environment config
cp .env.example .env

# 3. Start the stack
docker compose up -d

# 4. Open in browser
#    http://localhost:8080
```

The **first user to register** automatically becomes the **admin** with full access to all platform features.

> **Note:** On first start, the database initialization takes ~60 seconds. If the app container exits, just run `docker compose up -d` again — PostgreSQL will already be initialized and startup will be instant.

---

## ✨ Features

### Visual Workflow Designer
- **Drag-and-drop canvas** with SVG-based node rendering
- **Real-time execution** with live status visualization on every node
- **Bezier connections** with tag-based conditional routing (success/error/custom)
- **Undo/redo**, zoom, pan, and keyboard shortcuts
- **Workflow import/export** as JSON for version control and sharing

### 46+ Pre-Built Custom Nodes
Pre-loaded node library covering common automation tasks:

| Category | Nodes |
|----------|-------|
| 🔄 **Data Transformation** | Base64, CSV↔JSON, XML↔JSON, YAML↔JSON, JSONPath Query, String Template, Hash Generator, Date Math, Cron Parser, Regex, Pivot Table, and more |
| 🤖 **AI & Machine Learning** | OpenAI, Anthropic Claude, Google Gemini, DeepSeek, Groq, Mistral, GitHub Copilot, OpenClaw Bridge |
| 🌐 **Web & HTTP** | HTTP Request/Response, Webhook Listeners, File Download |
| 🔧 **Platform Tools** | Scheduler, Cache, Loop, Condition, Aggregator, Queue, Remote Execution |
| ☁️ **Azure Services** | Azure Blob Storage, Azure Storage Tables, Azure Queue |
| 📊 **Microsoft 365** | OneDrive Trigger, Excel to JSON |
| 📈 **Microsoft Graph** | Users, Groups, Group Members |
| 🤝 **Microsoft Partner Center** | Customers, Subscriptions, Invoices, Resellers, Billing Profiles |

### AI-Powered Workflow Builder
- **Natural language → workflow generation** — describe what you want, get a working workflow
- Context-aware node suggestion
- Automatic spatial layout of generated workflows

### Organizations & Collaboration
- **Multi-tenant organizations** with role-based access (Founder, Owner, Contributor)
- Shared workflows, secrets, and storage scoped per organization
- Organization-scoped execution isolation — one org can't see another's data

### Knowledge Base (RAG)
- **Built-in vector database** for Retrieval-Augmented Generation
- Document ingestion with PDF OCR support
- Context-aware AI nodes with automatic knowledge retrieval

### Additional Platform Features
- 📊 Execution logging with configurable retention
- 🔐 Encrypted secrets & connection management
- 🔄 Cron-based workflow scheduling
- 🖥️ Remote agent execution via Python and PowerShell clients
- 🎨 Custom node development via JavaScript (Jint runtime)
- 🔑 OAuth connector catalog (Microsoft, Google, GitHub)
- 📦 Workflow sample library

---

## 📖 Detailed Setup Guide

### Prerequisites

| Requirement | Version | Purpose |
|-------------|---------|---------|
| [Docker Desktop](https://www.docker.com/products/docker-desktop) | 20+ | Container runtime |
| [Docker Compose](https://docs.docker.com/compose/install/) | v2+ | Multi-container orchestration |
| Git | Any recent | Clone the repository |

> **Hardware:** S2G Pulse runs comfortably with 2 GB RAM and 2 CPU cores. PostgreSQL uses ~256 MB at idle.

### Step 1: Clone and Configure

```bash
git clone https://github.com/helmutsreinis/S2G-Pulse-Community.git
cd S2G-Pulse-Community
```

Copy the example environment file and edit it:

```bash
cp .env.example .env
```

Open `.env` in your editor and configure:

```env
# ─── REQUIRED ───────────────────────────────────────────────
# PostgreSQL password — change this from the default!
POSTGRES_PASSWORD=my_secure_password_123

# Port to expose S2G Pulse on (default: 8080)
S2G_PORT=8080

# ─── OPTIONAL ───────────────────────────────────────────────
# Admin email — if empty, the first user to register gets admin rights
ADMIN_EMAIL=

# Microsoft SSO (Azure AD / Entra ID)
# Create an app registration at https://portal.azure.com → App registrations
# Redirect URI: http://localhost:8080/signin-microsoft
AUTHENTICATION__MICROSOFT__CLIENTID=
AUTHENTICATION__MICROSOFT__CLIENTSECRET=

# Google SSO
# Create credentials at https://console.cloud.google.com → APIs & Services → Credentials
# Redirect URI: http://localhost:8080/signin-google
AUTHENTICATION__GOOGLE__CLIENTID=
AUTHENTICATION__GOOGLE__CLIENTSECRET=
```

### Step 2: Start the Stack

```bash
docker compose up -d
```

This starts two containers:
1. **`postgres`** — PostgreSQL 16 with a persistent data volume
2. **`s2g-pulse`** — The S2G Pulse web application

On **first start**, the system will automatically:
- Run database migrations (create all tables)
- Seed the **Self-Hosted Unlimited** membership plan
- Seed **10 node categories** with built-in nodes
- Import **46 custom node definitions** from the node library
- Create default legal documents

This process takes **30–90 seconds** depending on your hardware.

### Step 3: Verify the Stack

```bash
# Check container status
docker compose ps

# Check application health
curl http://localhost:8080/health

# View application logs
docker compose logs s2g-pulse --tail 50
```

You should see:
```
s2g-pulse-1  | Membership plans seeding completed.
s2g-pulse-1  | Seeded 10 node categories
s2g-pulse-1  | Custom node seeding complete: 46 imported, 0 skipped from 46 files
s2g-pulse-1  | Node categories and custom nodes seeding completed.
```

### Step 4: Register and Start Building

1. Open **http://localhost:8080** in your browser
2. Click **Register** and create your account
3. ✅ The first registered user automatically becomes the **admin**
4. Navigate to **Workflows** → **Create New Workflow**
5. Start dragging nodes from the palette onto the canvas!

### Common Operations

```bash
# Stop (keeps data)
docker compose down

# Restart
docker compose up -d

# View live logs
docker compose logs -f s2g-pulse

# Full reset (deletes all data!)
docker compose down -v
docker compose up -d

# Update to latest version
git pull
docker compose build --no-cache s2g-pulse
docker compose up -d --force-recreate s2g-pulse
```

### Troubleshooting

| Problem | Solution |
|---------|----------|
| App won't start | Run `docker compose up -d` again — PostgreSQL may need a second attempt on first init |
| Port 8080 in use | Change `S2G_PORT` in `.env` to another port (e.g., `9090`) |
| Database errors | `docker compose down -v && docker compose up -d` to reset |
| "Upgrade your plan" | This shouldn't happen in self-hosted mode. If it does, check that `SelfHosted: true` is set in `appsettings.Docker.json` |
| SSO not working | Make sure redirect URIs in your OAuth app match your host/port exactly |

### Reverse Proxy (Production)

For production deployments behind nginx or Traefik:

```nginx
server {
    listen 443 ssl;
    server_name pulse.yourdomain.com;

    location / {
        proxy_pass http://localhost:8080;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

> **Important:** WebSocket support (`Upgrade` + `Connection` headers) is required for real-time execution visuals and the OpenClaw AI integration.

---

## 🐾 OpenClaw — AI Agent Integration

S2G Pulse includes native integration with **[OpenClaw](https://openclaw.ai)** through the **OpenClaw Bridge Node**. This turns your workflows into callable tools for AI agents.

### What is OpenClaw?

OpenClaw is an open standard for connecting AI agents to real-world tools via WebSocket. The S2G Pulse OpenClaw node acts as a **bridge** — it exposes your workflow's nodes as tools that any OpenClaw-compatible AI agent can discover and invoke in real-time.

### How it Works

```
┌────────────────┐        WebSocket         ┌─────────────────────────┐
│   AI Agent     │ ◄────────────────────►   │    S2G Pulse Workflow   │
│  (OpenClaw)    │    /api/openclaw/ws/{id}  │                         │
│                │                          │  ┌─────────────────┐    │
│  1. Connect    │─────────────────────────► │  │  OpenClaw Node  │    │
│  2. List tools │◄─────────────────────────│  │  (Bridge)       │    │
│  3. Call tool  │─────────────────────────► │  └────────┬────────┘    │
│  4. Get result │◄─────────────────────────│           │             │
│                │                          │  ┌────────▼────────┐    │
│                │                          │  │  HTTP Request   │    │
│                │                          │  │  Data Transform │    │
│                │                          │  │  AI Inference   │    │
│                │                          │  │  ... any node   │    │
└────────────────┘                          │  └─────────────────┘    │
                                            └─────────────────────────┘
```

### Setting Up OpenClaw

1. **Add an OpenClaw node** to your workflow canvas
2. **Connect downstream nodes** — each connected node becomes an available "tool"
3. **Start the workflow** — the OpenClaw WebSocket endpoint activates
4. **Connect your AI agent** to `ws://your-server/api/openclaw/ws/{nodeId}`
5. The agent can now **discover** and **call** your workflow nodes as tools

### Features
- **Live View** — monitor WebSocket messages in real-time from the node editor
- **Input Forwarding** — automatically push upstream data to connected agents
- **Tool Discovery** — agents receive the full list of available nodes with their input/output schemas
- **Bidirectional** — agents call tools and receive results in the same WebSocket session

### 🧠 AI Agent Skills on ClawHub

We've published a curated set of **AI Agent skills** built with S2G Pulse workflows on ClawHub:

👉 **[s2g-workflow-engine on ClawHub](https://clawhub.ai/helmutsreinis/s2g-workflow-engine)**

These skills demonstrate how to build production-ready AI agent capabilities using the S2G Pulse workflow engine, including:
- Data transformation pipelines as agent tools
- API integration skills (Microsoft Graph, Partner Center)
- Multi-step reasoning workflows
- Knowledge retrieval and RAG patterns

You can use these as starting points for your own AI agent integrations.

---

## 🔧 Custom Nodes

S2G Pulse includes a powerful custom node system built on the **Jint JavaScript engine**. Nodes are defined as JSON files in the `custom-nodes/` directory and are automatically imported on first start.

### Creating Custom Nodes

1. Open the **Admin** panel → **Node Designer**
2. Define your node:
   - **Input fields** — Text, TextArea, Dropdown, Number, Toggle
   - **Output parameters** — Named outputs accessible by downstream nodes via `{{NodeName.ParamName}}`
   - **Connection tags** — Route execution flow (e.g., `success`, `error`, `timeout`)
   - **JavaScript logic** — Full Jint runtime with `input.get()`, `output.set()`, `tags.trigger()`, `log.info()` APIs
3. **Test** using the built-in test panel
4. **Export** as JSON for version control

### Node JavaScript API

```javascript
// Read input fields
var name = input.get("fieldName");

// Set output parameters
output.set("result", processedData);
output.set("count", items.length);

// Trigger connection tags (controls flow routing)
tags.trigger("success");    // follows the "success" connection
tags.trigger("error");      // follows the "error" connection

// Logging
log.info("Processing " + name);
log.warn("Unexpected format");
log.error("Failed: " + ex.message);
```

### Node JSON Format

```json
{
  "exportVersion": 1,
  "definition": {
    "nodeTypeKey": "Custom_MyNode",
    "displayName": "My Custom Node",
    "categoryName": "Data Transformation",
    "executionType": "DataTransformation",
    "timeoutSeconds": 30,
    "script": "var data = input.get('data');\noutput.set('result', data.toUpperCase());\ntags.trigger('success');",
    "inputFields": [
      {
        "fieldName": "data",
        "displayLabel": "Input Data",
        "fieldType": "TextArea",
        "isRequired": true,
        "allowPlaceholders": true,
        "displayOrder": 1
      }
    ],
    "outputParameters": [
      { "parameterName": "result", "dataType": "string", "displayOrder": 1 }
    ],
    "connectionTags": [
      { "tagName": "success", "color": "#22c55e", "displayOrder": 1 },
      { "tagName": "error", "color": "#ef4444", "displayOrder": 2 }
    ]
  }
}
```

### Adding Nodes to the Distribution

Drop your exported `.json` files into `custom-nodes/` — they'll be automatically imported on fresh installs.

---

## 🏗️ Architecture

```
┌─────────────────────────────────────────────┐
│              S2G Pulse Web                  │
│          (.NET 9 Blazor Server)             │
│                                             │
│  ┌──────────┐  ┌───────────┐  ┌──────────┐ │
│  │ Workflow  │  │    AI     │  │  Admin   │ │
│  │ Designer  │  │  Builder  │  │  Panel   │ │
│  └──────────┘  └───────────┘  └──────────┘ │
│                                             │
│  ┌──────────────────────────────────────┐   │
│  │     Jint JavaScript Engine           │   │
│  │  (Custom Node Execution Runtime)     │   │
│  └──────────────────────────────────────┘   │
│                                             │
│  ┌────────────────┐  ┌──────────────────┐   │
│  │  OpenClaw WS   │  │  HTTP Listener   │   │
│  │  Bridge        │  │  Endpoints       │   │
│  └────────────────┘  └──────────────────┘   │
│                                             │
│  ┌──────────────────────────────────────┐   │
│  │     Entity Framework Core            │   │
│  └──────────────────────────────────────┘   │
└──────────────────┬──────────────────────────┘
                   │
         ┌─────────┴─────────┐
         │   PostgreSQL 16   │
         │  (Docker Volume)  │
         └───────────────────┘
```

### Tech Stack
| Layer | Technology |
|-------|-----------|
| **Runtime** | .NET 9 with Blazor Server (interactive SSR) |
| **Database** | PostgreSQL 16 (Alpine) via Entity Framework Core |
| **Script Engine** | Jint (JavaScript interpreter for .NET) |
| **Container** | Docker + Docker Compose |
| **Frontend** | Blazor components with custom SVG canvas |
| **AI Bridge** | OpenClaw WebSocket protocol |
| **Remote Execution** | Python/PowerShell agents via Azure Function proxy |

### Project Structure

```
S2G-Run-Community/
├── S2GPulseWeb.Web/              # Main Blazor Server application
│   ├── Components/Pages/         # Razor pages (Home, Workflow, Admin, Organizations)
│   │   └── Workflow/
│   │       ├── Designer/         # Canvas, catalog, node editors
│   │       └── NodeEditors/      # Per-node-type editor UIs (incl. OpenClawEditor)
│   ├── Logic/                    # Business services
│   │   ├── Nodes/                # Built-in node executors (OpenClawNode, etc.)
│   │   ├── CustomNodeService.cs  # Custom node CRUD + seeding
│   │   ├── OrganizationService.cs
│   │   └── ...
│   ├── Data/                     # Entity models & DbContext
│   ├── Controllers/              # API endpoints (catalog, OpenClaw WS)
│   ├── Dockerfile                # Container definition
│   └── appsettings.Docker.json   # Self-hosted default config
├── S2GPulseWeb.ServiceDefaults/  # Shared service configuration
├── AzureFunctionProxy/           # Azure Function proxy for remote nodes
├── custom-nodes/                 # 46 pre-built custom node JSON definitions
├── workflow-samples/             # Example workflow JSON files
├── clients/                      # Remote agent scripts (Python/PowerShell)
├── docker-compose.yml            # Docker Compose stack definition
├── .env.example                  # Environment config template
└── LICENSE                       # Unlicense (Public Domain)
```

---

## 🛠️ Local Development (Without Docker)

### Prerequisites
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- PostgreSQL 16 (local install or Docker)

### Quick Start

```bash
# 1. Start PostgreSQL
docker run -d --name pulse-pg \
  -e POSTGRES_DB=pulsewebdb \
  -e POSTGRES_PASSWORD=dev \
  -p 5432:5432 \
  postgres:16-alpine

# 2. Create a development settings file
#    Copy appsettings.Docker.json → appsettings.Development.json
#    Update the connection string to Host=localhost

# 3. Run the application
cd S2GPulseWeb.Web
dotnet run

# 4. Open http://localhost:5000
```

---

## 🤝 Contributing

Contributions are welcome! This project is licensed under the [PolyForm Noncommercial License 1.0.0](LICENSE) — free for non-commercial use.

### How to Contribute

1. **Fork** this repository
2. Create a **feature branch** (`git checkout -b feature/amazing-feature`)
3. **Commit** your changes (`git commit -m 'Add amazing feature'`)
4. **Push** to the branch (`git push origin feature/amazing-feature`)
5. Open a **Pull Request**

### Ideas for Contributions
- 🧩 New custom node definitions (drop a `.json` in `custom-nodes/`)
- 🎨 UI/UX improvements
- 🤖 Additional AI provider integrations
- 🐾 OpenClaw agent skills and workflow templates
- 📝 Documentation and tutorials
- 🐛 Bug fixes and performance optimizations
- 🌍 Internationalization (i18n)

---

## 📄 License

This project is licensed under the [PolyForm Noncommercial License 1.0.0](LICENSE).

**You are free to:**
- Use, modify, and distribute for **personal, educational, and non-commercial** purposes
- Use within charitable, educational, research, and government organizations

**You may not:**
- Use for **commercial purposes** without separate written permission from the licensor
- Sell, license, or offer the software as a commercial product or service

---

<div align="center">

**Built with ❤️ by [S2G](https://s2g.run) — Just Run It**

*Made in Latvia 🇱🇻 | EU Data Residency | Non-Commercial Use Only*

[Website](https://s2g.run) • [ClawHub Skills](https://clawhub.ai/helmutsreinis/s2g-workflow-engine) • [OpenClaw](https://openclaw.ai)

</div>