# AutoCopilot Model Proxy - Design Document

## Overview

A unified model proxy service that aggregates OpenAI and Anthropic third-party models, exposing them through a single endpoint with an "AutoCopilot" shadow model that can dynamically route to any configured backend model.

## Tech Stack

- **Backend**: ASP.NET Core 10 Web API
- **Frontend**: Pure HTML/CSS/JS in `wwwroot/`
- **Metrics Storage**: SQLite (lightweight, no external dependencies)
- **Configuration**: JSON file persistence
- **Charts**: Chart.js (CDN)

## Project Structure

```
copilot-auto-byok/
├── Models/
│   ├── ProviderConfig.cs           # Provider configuration
│   ├── AutoCopilotBinding.cs       # AutoCopilot binding state
│   ├── AppConfiguration.cs         # Full application config
│   ├── ApiKeyConfig.cs             # API key authentication config
│   └── Metrics/
│       ├── RequestMetrics.cs       # Per-request metrics record
│       └── MetricsSummary.cs       # Aggregated statistics
├── Services/
│   ├── IProxyService.cs            # Proxy interface
│   ├── ProxyService.cs             # Request forwarding + metrics collection
│   ├── IConfigService.cs           # Config management interface
│   ├── ConfigService.cs            # JSON config persistence
│   ├── IMetricsService.cs          # Metrics service interface
│   └── MetricsService.cs           # SQLite metrics storage/query
├── Controllers/
│   ├── OpenAIController.cs         # /v1/chat/completions
│   ├── AnthropicController.cs      # /v1/messages
│   ├── AdminController.cs          # /api/config, /api/autocopilot
│   └── MetricsController.cs        # /api/metrics
├── Middleware/
│   ├── AuthMiddleware.cs           # API key authentication
│   └── StreamResult.cs             # Streaming response handler
├── Data/
│   ├── models.json                 # Runtime configuration
│   └── metrics.db                  # SQLite metrics database
├── wwwroot/
│   ├── index.html                  # Main SPA
│   ├── css/
│   │   └── style.css
│   └── js/
│       ├── config.js               # Config management
│       ├── autocopilot.js          # AutoCopilot control
│       └── metrics.js              # Metrics dashboard
├── Program.cs
├── appsettings.json
└── copilot-auto-byok.csproj
```

## Core Concepts

### AutoCopilot Shadow Model

- A virtual model name exposed as `AutoCopilot`
- Can be dynamically bound to any configured real model (e.g., gpt-4o, claude-3-5-sonnet)
- Switching takes effect immediately without restart
- All requests to `AutoCopilot` are transparently forwarded to the bound model

### Protocol Support

- **OpenAI Compatible**: `/v1/chat/completions`, `/v1/models`
- **Anthropic Native**: `/v1/messages`
- Protocols are NOT converted between each other; each is proxied independently

## API Design

### Proxy Endpoints

| Route | Method | Description |
|-------|--------|-------------|
| `/v1/chat/completions` | POST | OpenAI compatible chat completions |
| `/v1/models` | GET | List all available models (including AutoCopilot) |
| `/v1/messages` | POST | Anthropic messages endpoint |

### Admin Endpoints

| Route | Method | Description |
|-------|--------|-------------|
| `/api/config` | GET | Get full configuration |
| `/api/config` | PUT | Update configuration |
| `/api/autocopilot` | GET | Get AutoCopilot current binding |
| `/api/autocopilot` | PUT | Set AutoCopilot binding |
| `/api/keys` | GET | List API keys |
| `/api/keys` | POST | Add API key |
| `/api/keys/{id}` | DELETE | Remove API key |

### Metrics Endpoints

| Route | Method | Description |
|-------|--------|-------------|
| `/api/metrics/requests` | GET | Paginated request logs |
| `/api/metrics/summary` | GET | Aggregated statistics (1h/24h/7d/30d) |
| `/api/metrics/realtime` | GET | Real-time stats |
| `/api/metrics/export` | GET | Export CSV |

## Data Models

### Configuration (models.json)

```json
{
  "providers": {
    "openai": {
      "apiKey": "sk-xxx",
      "baseUrl": "https://api.openai.com",
      "models": ["gpt-4o", "gpt-4o-mini", "gpt-3.5-turbo"]
    },
    "anthropic": {
      "apiKey": "sk-ant-xxx",
      "baseUrl": "https://api.anthropic.com",
      "models": ["claude-3-5-sonnet-20241022", "claude-3-opus-20240229"]
    }
  },
  "autoCopilot": {
    "currentModel": "gpt-4o",
    "currentProvider": "openai"
  },
  "apiKeys": [
    { "key": "proxy-key-1", "name": "Default Key", "createdAt": "2026-06-04T00:00:00Z" }
  ]
}
```

### Request Metrics (SQLite)

```sql
CREATE TABLE request_metrics (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp TEXT NOT NULL,
    requested_model TEXT NOT NULL,
    actual_model TEXT NOT NULL,
    provider TEXT NOT NULL,
    protocol TEXT NOT NULL,
    is_streaming INTEGER NOT NULL,
    prompt_tokens INTEGER NOT NULL DEFAULT 0,
    completion_tokens INTEGER NOT NULL DEFAULT 0,
    total_tokens INTEGER NOT NULL DEFAULT 0,
    latency_ms INTEGER NOT NULL,
    total_duration_ms INTEGER NOT NULL,
    tokens_per_second REAL NOT NULL,
    is_cache_hit INTEGER NOT NULL DEFAULT 0,
    status_code INTEGER NOT NULL,
    is_success INTEGER NOT NULL,
    error TEXT
);
```

## Metrics Collected

| Metric | Description |
|--------|-------------|
| TTFT (Time to First Token) | Latency from request sent to first response byte |
| Total Duration | End-to-end request time |
| Tokens/Second | Generation speed |
| Prompt Tokens | Input token count |
| Completion Tokens | Output token count |
| Total Tokens | Sum of prompt + completion |
| Cache Hit Rate | Provider cache utilization |
| Success Rate | Request success/failure ratio |
| Estimated Cost | Based on model pricing tables |

## Authentication

- Proxy endpoints require `Authorization: Bearer <api-key>` header
- API keys are configurable via the admin UI
- Multiple keys supported for rotation/different clients
- Admin endpoints can use the same keys or be separately secured

## Frontend Design

Single-page application with three tabs:

### Tab 1: Configuration
- API Key management (add/remove keys)
- OpenAI provider config (API Key, Base URL, model list)
- Anthropic provider config (API Key, Base URL, model list)
- Save button

### Tab 2: AutoCopilot
- Current binding display (large card)
- Dropdown to select target model
- Switch button
- Binding history (last 10 changes)

### Tab 3: Metrics Dashboard
- Summary cards: total requests, success rate, total tokens, estimated cost
- Time range selector: 1h / 24h / 7d / 30d / custom
- Charts (Chart.js):
  - Request volume trend
  - Token consumption bar chart
  - Latency distribution
  - Model usage pie chart
- Request log table (filterable, paginated, CSV export)

## Key Implementation Details

1. **Streaming Support**: Use `HttpCompletionOption.ResponseHeadersRead` to avoid buffering, pipe SSE directly
2. **Hot Reload**: Config changes apply immediately via atomic replacement of in-memory cache
3. **Metrics Collection**: Intercept streaming responses to parse usage data; calculate TTFT from stopwatch
4. **Cache Detection**: Parse OpenAI `cache_read_tokens` or Anthropic `cache_control` fields
5. **Cost Estimation**: Built-in price table per model, auto-calculated from token usage
6. **Error Handling**: Provider errors passed through; local errors wrapped with context
