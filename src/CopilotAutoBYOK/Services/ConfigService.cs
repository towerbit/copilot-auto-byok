using System.Data.Common;
using System.Security;
using copilot_auto_byok.Data;
using copilot_auto_byok.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace copilot_auto_byok.Services;

public class ConfigService : IConfigService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ConfigService> _logger;
    private readonly object _autoPilotLock = new();
    private readonly object _cacheLock = new();

    // Cache keys
    private const string CacheApiKeys = "api_keys";
    private const string CacheProviders = "providers";
    private const string CacheAutoCopilot = "autocopilot";
    private const string CacheByokEnv = "byok_env";

    // Cache expiration
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(5);

    public ConfigService(IDbContextFactory<AppDbContext> contextFactory, IMemoryCache cache, ILogger<ConfigService> logger)
    {
        _contextFactory = contextFactory;
        _cache = cache;
        _logger = logger;

        // Ensure database is created
        using var context = _contextFactory.CreateDbContext();
        context.Database.EnsureCreated();
        EnsureAutoCopilotDualBindingColumns(context);

        // Apply persisted BYOK env on startup in background to avoid blocking
        var byok = context.ByokEnv.OrderBy(e => e.Id).FirstOrDefault();
        if (byok != null && !string.IsNullOrWhiteSpace(byok.ProviderBaseUrl))
        {
            var config = MapToModel(byok);
            Task.Run(() => ApplyByokEnvToUser(config));
        }
    }

    public AppConfiguration GetConfiguration()
    {
        return new AppConfiguration
        {
            Providers = GetProviders(),
            AutoCopilot = GetAutoCopilotBinding(),
            ApiKeys = GetApiKeys(),
            ByokEnv = GetByokEnv()
        };
    }

    public void SaveConfiguration(AppConfiguration config)
    {
        // Not used with EF Core — individual updates are preferred
    }

    public List<ProviderConfig> GetProviders()
    {
        if (_cache.TryGetValue(CacheProviders, out List<ProviderConfig>? cached))
            return cached ?? new();

        lock (_cacheLock)
        {
            if (_cache.TryGetValue(CacheProviders, out cached))
                return cached ?? new();

            using var context = _contextFactory.CreateDbContext();
            var providers = context.Providers.AsNoTracking().Select(p => MapToModel(p)).ToList();

            _cache.Set(CacheProviders, providers, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = CacheExpiration,
                Size = 1
            });

            return providers;
        }
    }

    public ProviderConfig? GetProvider(string id)
    {
        var providers = GetProviders();
        return providers.FirstOrDefault(p => p.Id == id);
    }

    public void AddProvider(ProviderConfig provider)
    {
        using var context = _contextFactory.CreateDbContext();
        if (string.IsNullOrEmpty(provider.Id))
            provider.Id = Guid.NewGuid().ToString("N");
        if (provider.CreatedAt == default)
            provider.CreatedAt = DateTime.UtcNow;

        var providerName = provider.Name.Trim();
        var nameExists = context.Providers.Any(p => p.Name == providerName);
        if (nameExists)
            throw new InvalidOperationException($"Provider name '{providerName}' already exists.");

        provider.Name = providerName;
        context.Providers.Add(MapToEntity(provider));
        context.SaveChanges();

        _cache.Remove(CacheProviders);
    }

    public void UpdateProvider(ProviderConfig provider)
    {
        using var context = _contextFactory.CreateDbContext();
        var entity = context.Providers.FirstOrDefault(p => p.Id == provider.Id);
        if (entity == null) return;

        var providerName = provider.Name.Trim();
        var nameExists = context.Providers.Any(p => p.Id != provider.Id && p.Name == providerName);
        if (nameExists)
            throw new InvalidOperationException($"Provider name '{providerName}' already exists.");

        entity.Name = providerName;
        entity.Type = provider.Type;
        entity.BaseUrl = provider.BaseUrl;
        entity.ApiKey = provider.ApiKey;
        entity.SetModels(provider.Models);
        entity.SetVisibleModels(provider.VisibleModels);
        entity.Description = provider.Description;
        context.SaveChanges();

        _cache.Remove(CacheProviders);
    }

    public void DeleteProvider(string id)
    {
        using var context = _contextFactory.CreateDbContext();
        var entity = context.Providers.FirstOrDefault(p => p.Id == id);
        if (entity == null) return;
        context.Providers.Remove(entity);
        context.SaveChanges();

        _cache.Remove(CacheProviders);
    }

    public AutoCopilotBinding GetAutoCopilotBinding()
    {
        if (_cache.TryGetValue(CacheAutoCopilot, out AutoCopilotBinding? cached))
            return cached!;

        lock (_cacheLock)
        {
            if (_cache.TryGetValue(CacheAutoCopilot, out cached))
                return cached!;

            using var context = _contextFactory.CreateDbContext();
            var entity = context.AutoCopilot.OrderBy(e => e.Id).FirstOrDefault();
            if (entity != null)
            {
                var binding = MapToAutoCopilotBinding(entity);
                _cache.Set(CacheAutoCopilot, binding, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = CacheExpiration,
                    Size = 1
                });
                return binding;
            }

            // Entity not yet seeded — create it (under lock to avoid duplicate rows)
            lock (_autoPilotLock)
            {
                entity = context.AutoCopilot.OrderBy(e => e.Id).FirstOrDefault();
                if (entity != null)
                {
                    var binding = MapToAutoCopilotBinding(entity);
                    _cache.Set(CacheAutoCopilot, binding, new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = CacheExpiration,
                        Size = 1
                    });
                    return binding;
                }

                entity = new AutoCopilotBindingEntity();
                context.AutoCopilot.Add(entity);
                context.SaveChanges();

                var newBinding = MapToAutoCopilotBinding(entity);
                _cache.Set(CacheAutoCopilot, newBinding, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = CacheExpiration,
                    Size = 1
                });
                return newBinding;
            }
        }
    }

    public void UpdateAutoCopilotBinding(AutoCopilotBinding binding)
    {
        using var context = _contextFactory.CreateDbContext();
        var entity = context.AutoCopilot.OrderBy(e => e.Id).FirstOrDefault();
        if (entity == null)
        {
            entity = new AutoCopilotBindingEntity();
            context.AutoCopilot.Add(entity);
        }
        entity.OpenAICurrentModel = binding.OpenAICurrentModel;
        entity.OpenAICurrentProviderId = binding.OpenAICurrentProviderId;
        entity.AnthropicCurrentModel = binding.AnthropicCurrentModel;
        entity.AnthropicCurrentProviderId = binding.AnthropicCurrentProviderId;
        context.SaveChanges();

        _cache.Remove(CacheAutoCopilot);
    }

    private static AutoCopilotBinding MapToAutoCopilotBinding(AutoCopilotBindingEntity entity)
    {
        var openAIModel = entity.OpenAICurrentModel ?? string.Empty;
        var openAIProviderId = entity.OpenAICurrentProviderId ?? string.Empty;
        var anthropicModel = entity.AnthropicCurrentModel;
        var anthropicProviderId = entity.AnthropicCurrentProviderId;

        return new AutoCopilotBinding
        {
            OpenAICurrentModel = openAIModel,
            OpenAICurrentProviderId = openAIProviderId,
            AnthropicCurrentModel = string.IsNullOrWhiteSpace(anthropicModel) ? openAIModel : anthropicModel,
            AnthropicCurrentProviderId = string.IsNullOrWhiteSpace(anthropicProviderId) ? openAIProviderId : anthropicProviderId
        };
    }

    public List<ApiKeyConfig> GetApiKeys()
    {
        if (_cache.TryGetValue(CacheApiKeys, out List<ApiKeyConfig>? cached))
            return cached ?? new();

        lock (_cacheLock)
        {
            if (_cache.TryGetValue(CacheApiKeys, out cached))
                return cached ?? new();

            using var context = _contextFactory.CreateDbContext();
            var keys = context.ApiKeys.AsNoTracking().Select(k => new ApiKeyConfig
            {
                Id = k.Id,
                Key = k.Key,
                Name = k.Name,
                CreatedAt = k.CreatedAt
            }).ToList();

            _cache.Set(CacheApiKeys, keys, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = CacheExpiration,
                Size = 1
            });

            return keys;
        }
    }

    public void AddApiKey(ApiKeyConfig key)
    {
        using var context = _contextFactory.CreateDbContext();
        context.ApiKeys.Add(new ApiKeyConfigEntity
        {
            Id = key.Id,
            Key = key.Key,
            Name = key.Name,
            CreatedAt = key.CreatedAt
        });
        context.SaveChanges();

        _cache.Remove(CacheApiKeys);
        _cache.Remove("valid_api_keys"); // Clear AuthMiddleware cache
    }

    public void RemoveApiKey(string id)
    {
        using var context = _contextFactory.CreateDbContext();
        var entity = context.ApiKeys.FirstOrDefault(k => k.Id == id);
        if (entity == null) return;
        context.ApiKeys.Remove(entity);
        context.SaveChanges();

        _cache.Remove(CacheApiKeys);
        _cache.Remove("valid_api_keys"); // Clear AuthMiddleware cache
    }

    public ByokEnvConfig GetByokEnv()
    {
        if (_cache.TryGetValue(CacheByokEnv, out ByokEnvConfig? cached))
            return cached!;

        lock (_cacheLock)
        {
            if (_cache.TryGetValue(CacheByokEnv, out cached))
                return cached!;

            using var context = _contextFactory.CreateDbContext();
            var entity = context.ByokEnv.OrderBy(e => e.Id).FirstOrDefault();
            var config = entity == null ? new ByokEnvConfig() : MapToModel(entity);

            _cache.Set(CacheByokEnv, config, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = CacheExpiration,
                Size = 1
            });

            return config;
        }
    }

    public void UpdateByokEnv(ByokEnvConfig config)
    {
        using var context = _contextFactory.CreateDbContext();
        var entity = context.ByokEnv.OrderBy(e => e.Id).FirstOrDefault();
        if (entity == null)
        {
            entity = new ByokEnvConfigEntity();
            context.ByokEnv.Add(entity);
        }
        entity.ProviderBaseUrl = config.ProviderBaseUrl;
        entity.ProviderType = config.ProviderType;
        entity.ProviderApiKey = config.ProviderApiKey;
        entity.ProviderBearerToken = config.ProviderBearerToken;
        entity.ProviderWireApi = config.ProviderWireApi;
        entity.ProviderAzureApiVersion = config.ProviderAzureApiVersion;
        entity.Model = config.Model;
        entity.ProviderModelId = config.ProviderModelId;
        entity.ProviderWireModel = config.ProviderWireModel;
        entity.ProviderMaxPromptTokens = config.ProviderMaxPromptTokens;
        entity.ProviderMaxOutputTokens = config.ProviderMaxOutputTokens;
        context.SaveChanges();

        _cache.Remove(CacheByokEnv);

        // Fire-and-forget: setting user env vars touches the Windows registry
        // and can block the request thread, so run it in the background.
        Task.Run(() => ApplyByokEnvToUser(config));
    }

    // ===== Mapping =====
    private static ProviderConfig MapToModel(ProviderConfigEntity e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        Type = e.Type,
        ApiKey = e.ApiKey,
        BaseUrl = e.BaseUrl,
        Models = e.GetModels(),
        VisibleModels = e.GetVisibleModels(),
        Description = e.Description,
        CreatedAt = e.CreatedAt
    };

    private static ProviderConfigEntity MapToEntity(ProviderConfig m) => new()
    {
        Id = m.Id,
        Name = m.Name,
        Type = m.Type,
        ApiKey = m.ApiKey,
        BaseUrl = m.BaseUrl,
        ModelsJson = System.Text.Json.JsonSerializer.Serialize(m.Models),
        VisibleModelsJson = System.Text.Json.JsonSerializer.Serialize(m.VisibleModels),
        Description = m.Description,
        CreatedAt = m.CreatedAt
    };

    private static ByokEnvConfig MapToModel(ByokEnvConfigEntity e) => new()
    {
        ProviderBaseUrl = e.ProviderBaseUrl,
        ProviderType = e.ProviderType,
        ProviderApiKey = e.ProviderApiKey,
        ProviderBearerToken = e.ProviderBearerToken,
        ProviderWireApi = e.ProviderWireApi,
        ProviderAzureApiVersion = e.ProviderAzureApiVersion,
        Model = e.Model,
        ProviderModelId = e.ProviderModelId,
        ProviderWireModel = e.ProviderWireModel,
        ProviderMaxPromptTokens = e.ProviderMaxPromptTokens,
        ProviderMaxOutputTokens = e.ProviderMaxOutputTokens
    };

    private void EnsureAutoCopilotDualBindingColumns(AppDbContext context)
    {
        var connection = context.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose)
            connection.Open();

        try
        {
            var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA table_info('AutoCopilot');";
                using var reader = pragma.ExecuteReader();
                while (reader.Read())
                {
                    existingColumns.Add(reader.GetString(1));
                }
            }

            EnsureColumn(connection, existingColumns, "OpenAICurrentModel", "TEXT");
            EnsureColumn(connection, existingColumns, "OpenAICurrentProviderId", "TEXT");
            EnsureColumn(connection, existingColumns, "AnthropicCurrentModel", "TEXT");
            EnsureColumn(connection, existingColumns, "AnthropicCurrentProviderId", "TEXT");

            using (var normalizeNullCommand = connection.CreateCommand())
            {
                normalizeNullCommand.CommandText = @"
UPDATE AutoCopilot
SET OpenAICurrentModel = COALESCE(OpenAICurrentModel, ''),
    OpenAICurrentProviderId = COALESCE(OpenAICurrentProviderId, ''),
    AnthropicCurrentModel = COALESCE(AnthropicCurrentModel, ''),
    AnthropicCurrentProviderId = COALESCE(AnthropicCurrentProviderId, '')
WHERE OpenAICurrentModel IS NULL
   OR OpenAICurrentProviderId IS NULL
   OR AnthropicCurrentModel IS NULL
   OR AnthropicCurrentProviderId IS NULL;";
                normalizeNullCommand.ExecuteNonQuery();
            }

            using var legacyBindingCommand = connection.CreateCommand();
            legacyBindingCommand.CommandText = @"
SELECT CurrentModel, CurrentProviderId,
       OpenAICurrentModel, OpenAICurrentProviderId,
       AnthropicCurrentModel, AnthropicCurrentProviderId
FROM AutoCopilot
WHERE Id = 1;";

            using var bindingReader = legacyBindingCommand.ExecuteReader();
            if (!bindingReader.Read())
                return;

            var currentModel = bindingReader.IsDBNull(0) ? "" : bindingReader.GetString(0);
            var currentProviderId = bindingReader.IsDBNull(1) ? "" : bindingReader.GetString(1);
            var openAIModel = bindingReader.IsDBNull(2) ? "" : bindingReader.GetString(2);
            var openAIProviderId = bindingReader.IsDBNull(3) ? "" : bindingReader.GetString(3);
            var anthropicModel = bindingReader.IsDBNull(4) ? "" : bindingReader.GetString(4);
            var anthropicProviderId = bindingReader.IsDBNull(5) ? "" : bindingReader.GetString(5);
            bindingReader.Close();

            if (string.IsNullOrWhiteSpace(currentModel) || string.IsNullOrWhiteSpace(currentProviderId))
                return;

            using var providerCommand = connection.CreateCommand();
            providerCommand.CommandText = "SELECT Type FROM Providers WHERE Id = $providerId LIMIT 1;";
            var providerIdParameter = providerCommand.CreateParameter();
            providerIdParameter.ParameterName = "$providerId";
            providerIdParameter.Value = currentProviderId;
            providerCommand.Parameters.Add(providerIdParameter);
            var providerType = providerCommand.ExecuteScalar() as string;

            if (string.IsNullOrWhiteSpace(providerType))
                return;

            var isOpenAI = string.Equals(providerType, "openai", StringComparison.OrdinalIgnoreCase);
            var isAnthropic = string.Equals(providerType, "anthropic", StringComparison.OrdinalIgnoreCase);
            if (!isOpenAI && !isAnthropic)
                return;

            if (isOpenAI && (!string.IsNullOrWhiteSpace(openAIModel) || !string.IsNullOrWhiteSpace(openAIProviderId)))
                return;

            if (isAnthropic && (!string.IsNullOrWhiteSpace(anthropicModel) || !string.IsNullOrWhiteSpace(anthropicProviderId)))
                return;

            using var updateCommand = connection.CreateCommand();
            if (isOpenAI)
            {
                updateCommand.CommandText = @"
UPDATE AutoCopilot
SET OpenAICurrentModel = $currentModel,
    OpenAICurrentProviderId = $currentProviderId
WHERE Id = 1;";
            }
            else
            {
                updateCommand.CommandText = @"
UPDATE AutoCopilot
SET AnthropicCurrentModel = $currentModel,
    AnthropicCurrentProviderId = $currentProviderId
WHERE Id = 1;";
            }

            var currentModelParameter = updateCommand.CreateParameter();
            currentModelParameter.ParameterName = "$currentModel";
            currentModelParameter.Value = currentModel;
            updateCommand.Parameters.Add(currentModelParameter);

            var currentProviderParameter = updateCommand.CreateParameter();
            currentProviderParameter.ParameterName = "$currentProviderId";
            currentProviderParameter.Value = currentProviderId;
            updateCommand.Parameters.Add(currentProviderParameter);

            updateCommand.ExecuteNonQuery();
        }
        finally
        {
            if (shouldClose)
                connection.Close();
        }
    }

    private void EnsureColumn(DbConnection connection, HashSet<string> existingColumns, string columnName, string columnType)
    {
        if (existingColumns.Contains(columnName))
            return;

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE AutoCopilot ADD COLUMN {columnName} {columnType};";
        alter.ExecuteNonQuery();
        existingColumns.Add(columnName);
        _logger.LogInformation("Added SQLite column {ColumnName} to AutoCopilot table.", columnName);
    }

    // ===== Environment Variables =====
    private static void ApplyByokEnvToUser(ByokEnvConfig config)
    {
        var target = EnvironmentVariableTarget.User;
        SetUserEnv("COPILOT_PROVIDER_BASE_URL", config.ProviderBaseUrl, target);
        SetUserEnv("COPILOT_PROVIDER_TYPE", config.ProviderType, target);
        SetUserEnv("COPILOT_PROVIDER_API_KEY", config.ProviderApiKey, target);
        SetUserEnv("COPILOT_PROVIDER_BEARER_TOKEN", config.ProviderBearerToken, target);
        SetUserEnv("COPILOT_PROVIDER_WIRE_API", config.ProviderWireApi, target);
        SetUserEnv("COPILOT_PROVIDER_AZURE_API_VERSION", config.ProviderAzureApiVersion, target);
        SetUserEnv("COPILOT_MODEL", config.Model, target);
        SetUserEnv("COPILOT_PROVIDER_MODEL_ID", config.ProviderModelId, target);
        SetUserEnv("COPILOT_PROVIDER_WIRE_MODEL", config.ProviderWireModel, target);
        SetUserEnv("COPILOT_PROVIDER_MAX_PROMPT_TOKENS", config.ProviderMaxPromptTokens?.ToString(), target);
        SetUserEnv("COPILOT_PROVIDER_MAX_OUTPUT_TOKENS", config.ProviderMaxOutputTokens?.ToString(), target);
    }

    private static void SetUserEnv(string name, string? value, EnvironmentVariableTarget target)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(value))
                Environment.SetEnvironmentVariable(name, value, target);
            else
                Environment.SetEnvironmentVariable(name, null, target);
        }
        catch (SecurityException)
        {
            // Insufficient privileges — silently skip
        }
    }
}
