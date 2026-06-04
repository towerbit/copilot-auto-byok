# copilot-auto-byok

[English](README.en-US.md) | [中文](README.zh-CN.md)

`copilot-auto-byok` is a custom backend proxy service for **GitHub Copilot** in **Bring Your Own Key (BYOK)** mode. It allows you to host your own OpenAI or Anthropic API keys locally and expose them as a Copilot-compatible backend, with flexible model routing, key management, and usage metrics.

The service provides OpenAI-compatible `/v1/chat/completions` and Anthropic-compatible `/v1/messages` proxy endpoints, plus a built-in web UI for managing providers, models, API keys, and request metrics.

## Features

- **Multi-provider support**: Manage OpenAI and Anthropic provider configurations in one place.
- **Auto model routing**: Use the `auto-copilot` binding to let Copilot automatically route to the active model.
- **API proxy**: Forward requests while staying compatible with OpenAI Chat Completions and Anthropic Messages APIs.
- **Key management**: Store and manage multiple upstream API keys or bearer tokens.
- **Usage metrics**: Built-in metrics collection for requests, tokens, and provider stats.
- **Admin UI**: Static web interface for configuration and monitoring.
- **Data migration**: Automatically migrate legacy `models.json` settings into SQLite on first run.

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

### Run

```bash
dotnet run --project copilot-auto-byok.csproj
```

The service listens on `http://localhost:5000` by default. Open the root path to access the admin UI.

### Environment Variables

You can configure the upstream BYOK connection via environment variables:

| Variable | Description | Default |
|----------|-------------|---------|
| `BYOK_PROVIDER_BASE_URL` | Upstream provider base URL | empty |
| `BYOK_PROVIDER_TYPE` | Provider type: `openai` or `anthropic` | `openai` |
| `BYOK_PROVIDER_API_KEY` | Upstream API key | empty |
| `BYOK_PROVIDER_BEARER_TOKEN` | Bearer token (takes precedence over API key) | empty |
| `BYOK_PROVIDER_WIRE_API` | Upstream API path: `completions` or `messages` | `completions` |
| `BYOK_MODEL` | Default model ID | `auto-copilot` |
| `BYOK_PROVIDER_MODEL_ID` | Actual upstream model ID | empty |
| `BYOK_PROVIDER_WIRE_MODEL` | Model name sent in upstream requests | empty |

## Configuration

On startup, the service persists configuration in `Data/app.db`:

- **Providers**: name, type, base URL, available models, visible models.
- **ApiKeys**: keys used to access this service.
- **AutoCopilot**: the current Copilot model and provider binding.
- **ByokEnv**: upstream connection and model mapping settings.

You can manage these through the admin UI or directly via API.

## API Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/v1/chat/completions` | OpenAI Chat Completions proxy |
| `POST` | `/v1/messages` | Anthropic Messages proxy |
| `GET` | `/v1/models` | List available models (including `auto-copilot`) |
| `GET` | `/metrics` | View request metrics |

## Admin UI

![Admin UI](docs/index.png)

## Metrics

![Metrics](docs/metrics.png)

## Tech Stack

- .NET 10
- ASP.NET Core
- Entity Framework Core + SQLite
- OpenAPI
- CORS

## Notes

- Do not commit `Data/app.db` to version control.
- Configure HTTPS and proper authentication in production.
