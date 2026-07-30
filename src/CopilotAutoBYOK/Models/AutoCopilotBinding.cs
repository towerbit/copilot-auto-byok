namespace copilot_auto_byok.Models;

public class AutoCopilotBinding
{
    public string OpenAICurrentModel { get; set; } = "";
    public string OpenAICurrentProviderId { get; set; } = "";
    public string AnthropicCurrentModel { get; set; } = "";
    public string AnthropicCurrentProviderId { get; set; } = "";
}
