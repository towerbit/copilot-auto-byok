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

        context.Providers.Add(MapToEntity(provider));
        context.SaveChanges();

        _cache.Remove(CacheProviders);
    }

    public void UpdateProvider(ProviderConfig provider)
    {
        using var context = _contextFactory.CreateDbContext();
        var entity = context.Providers.FirstOrDefault(p => p.Id == provider.Id);
        if (entity == null) return;

        entity.Name = provider.Name;
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
                var binding = new AutoCopilotBinding
                {
                    CurrentModel = entity.CurrentModel,
                    CurrentProviderId = entity.CurrentProviderId
                };
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
                    var binding = new AutoCopilotBinding
                    {
                        CurrentModel = entity.CurrentModel,
                        CurrentProviderId = entity.CurrentProviderId
                    };
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

                var newBinding = new AutoCopilotBinding
                {
                    CurrentModel = entity.CurrentModel,
                    CurrentProviderId = entity.CurrentProviderId
                };
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
        entity.CurrentModel = binding.CurrentModel;
        entity.CurrentProviderId = binding.CurrentProviderId;
        context.SaveChanges();

        _cache.Remove(CacheAutoCopilot);
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
