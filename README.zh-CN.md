# copilot-auto-byok

[English](README.en-US.md) | 中文

`copilot-auto-byok` 是一个用于 GitHub Copilot 的自定义后端代理服务，支持 **Bring Your Own Key (BYOK)** 模式。它允许你将 OpenAI 或 Anthropic 的 API Key 配置到本地服务中，作为 Copilot 的自定义模型后端，实现更灵活的模型选择、密钥管理和用量监控。

本服务提供 OpenAI 兼容的 `/v1/chat/completions` 和 Anthropic 兼容的 `/v1/messages` 代理端点，并内置管理界面用于配置提供商、模型、API Key 以及查看调用指标。

## 功能特性

- **多提供商支持**：同时管理 OpenAI 与 Anthropic 提供商配置。
- **自动模型切换**：通过 `auto-copilot` 绑定实现 Copilot 自动路由到当前模型。
- **API 代理转发**：兼容 OpenAI Chat Completions 与 Anthropic Messages 协议。
- **密钥管理**：集中存储与管理多组 API Key / Bearer Token。
- **用量指标**：内置 Metrics 服务，统计请求次数、Token 消耗等数据。
- **管理界面**：静态 Web 界面，可视化配置与监控。
- **数据迁移**：自动将旧版 `models.json` 迁移到 SQLite。

## 快速开始

### 环境要求

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

### 运行

```bash
dotnet run --project copilot-auto-byok.csproj
```

服务默认启动在 `http://localhost:5000`（或 ASP.NET Core 默认端口）。访问根路径即可打开管理界面。

### 环境变量

可通过环境变量配置 BYOK 基础连接信息：

| 变量 | 说明 | 默认值 |
|------|------|--------|
| `BYOK_PROVIDER_BASE_URL` | 上游提供商 Base URL | 空 |
| `BYOK_PROVIDER_TYPE` | 提供商类型：`openai` 或 `anthropic` | `openai` |
| `BYOK_PROVIDER_API_KEY` | 上游 API Key | 空 |
| `BYOK_PROVIDER_BEARER_TOKEN` | Bearer Token（优先于 API Key） | 空 |
| `BYOK_PROVIDER_WIRE_API` | 上游 API 路径：`completions` 或 `messages` | `completions` |
| `BYOK_MODEL` | 默认模型 ID | `auto-copilot` |
| `BYOK_PROVIDER_MODEL_ID` | 上游实际模型 ID | 空 |
| `BYOK_PROVIDER_WIRE_MODEL` | 上游请求中使用的模型名称 | 空 |

## 配置说明

启动后，服务会在 `Data/app.db` 中持久化以下配置：

- **Providers**：提供商名称、类型、Base URL、可用模型列表、可见模型列表。
- **ApiKeys**：用于访问本服务的 API Key。
- **AutoCopilot**：当前 Copilot 绑定的模型与提供商。
- **ByokEnv**：上游连接与模型映射配置。

管理界面提供了可视化操作入口，也可直接通过 API 进行读写。

## API 端点

| 方法 | 路径 | 说明 |
|------|------|------|
| `POST` | `/v1/chat/completions` | OpenAI Chat Completions 代理 |
| `POST` | `/v1/messages` | Anthropic Messages 代理 |
| `GET` | `/v1/models` | 获取可用模型列表（含 `auto-copilot`） |
| `GET` | `/metrics` | 查看调用指标 |

## 管理界面

![管理界面](docs/index.png)

## 指标监控

![指标监控](docs/metrics.png)

## 技术栈

- .NET 10
- ASP.NET Core
- Entity Framework Core + SQLite
- OpenAPI
- CORS
