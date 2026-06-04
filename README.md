# copilot-auto-byok

[English](README.en-US.md) | [中文](README.zh-CN.md)

![Admin UI](docs/index.png)

A lightweight proxy that lets **GitHub Copilot** use your own OpenAI or Anthropic keys in **BYOK** mode, with model routing, key management, and usage metrics.

## Quick start

```bash
dotnet run --project copilot-auto-byok.csproj
```

Open `http://localhost:5000` for the admin UI.

## Highlights

- OpenAI-compatible `/v1/chat/completions`
- Anthropic-compatible `/v1/messages`
- Built-in admin UI and metrics
- SQLite-backed config in `Data/app.db`

## Stack

- .NET 10
- ASP.NET Core
- EF Core + SQLite
- OpenAPI

## Docs

- 中文文档：`README.zh-CN.md`
- 英文文档：`README.md`
