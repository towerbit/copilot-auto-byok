# Copilot Instructions

## 项目指南
- 项目背景：copilot-auto-byok 是为了适配 VS2026 Copilot 通过 Ollama 协议开放自定义第三方 API 的能力。VS2026 Copilot 的鸡贼限制是用 Ollama 自然就不能配置 API Key，从而达到阻止任意 BYOK的目的。所以本项目支持不配 Key 访问（setup 模式），这是选择自建而不使用 sdcb/chats 的主要原因（后者需要 API Key，一样要改代码）。
- 项目使用 OpenAI 兼容的 /v1/chat/completions 和 Anthropic 兼容的 /v1/messages 端点，通过 ProxyService 转发请求，使用 ConfigService 管理配置。