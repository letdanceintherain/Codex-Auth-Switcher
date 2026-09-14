using System.Text.Json;
using CodexAuthSwitcher.Core.Models;
using CodexAuthSwitcher.Core.Utilities;

namespace CodexAuthSwitcher.Core.Services;

public sealed class ProfileStore
{
    private const string MetadataFileName = "profile.json";
    private const string ConfigSnapshotFileName = "config.toml";
    private const string AuthSnapshotFileName = "auth.json";
    private const string ProtectedAuthSnapshotFileName = "auth.bin";
    private const string SecretFileName = "secret.bin";
    private const string CurrentProfileFileName = "current-profile.json";

    private readonly ProtectedSecretStore _secretStore;
    private readonly LiveAuthInspector _liveAuthInspector;

    public ProfileStore(string codexHomePath, ProtectedSecretStore secretStore, LiveAuthInspector liveAuthInspector)
    {
        CodexHomePath = codexHomePath;
        _secretStore = secretStore;
        _liveAuthInspector = liveAuthInspector;
    }

    public string CodexHomePath { get; }
    public string RootPath => Path.Combine(CodexHomePath, "auth-switcher");
    public string ProfilesPath => Path.Combine(RootPath, "profiles");
    public string BackupsPath => Path.Combine(RootPath, "backups");
    public string CurrentProfileStatePath => Path.Combine(RootPath, CurrentProfileFileName);

    public void EnsureLayout()
    {
        PathHelpers.EnsureDirectory(RootPath);
        PathHelpers.EnsureDirectory(ProfilesPath);
        PathHelpers.EnsureDirectory(BackupsPath);
    }

    public IReadOnlyList<ProfileSummary> GetProfiles()
    {
        EnsureLayout();
        var items = new List<ProfileSummary>();
        foreach (var directory in Directory.EnumerateDirectories(ProfilesPath).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var summary = LoadProfileSummary(directory);
            if (summary is not null)
            {
                items.Add(summary);
            }
        }

        return items;
    }

    public ProfileSummary? FindProfileByName(string profileName)
    {
        return GetProfiles().FirstOrDefault(item => string.Equals(item.Name, profileName, StringComparison.OrdinalIgnoreCase));
    }

    public ProfileSummary? FindProfileByIdentityFingerprint(string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return null;
        }

        return GetProfiles().FirstOrDefault(item =>
            string.Equals(item.IdentityFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
    }

    public string CreateUniqueProfileName(LiveAuthIdentity identity)
    {
        EnsureLayout();

        var baseName = identity.Kind == ProfileKind.ChatGptSnapshot
            ? CreateChatGptBaseName(identity)
            : CreateApiBaseName(identity);
        var sanitizedBaseName = PathHelpers.SanitizeProfileName(baseName);
        var existingNames = new HashSet<string>(
            Directory.EnumerateDirectories(ProfilesPath).Select(Path.GetFileName).Where(name => !string.IsNullOrWhiteSpace(name))!,
            StringComparer.OrdinalIgnoreCase);

        if (!existingNames.Contains(sanitizedBaseName))
        {
            return sanitizedBaseName;
        }

        for (var index = 2; index < 10_000; index++)
        {
            var candidate = $"{sanitizedBaseName}-{index}";
            if (!existingNames.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Unable to allocate a unique profile name.");
    }

    public ApiProfileSpec? LoadApiProfile(string profileName)
    {
        var profilePath = GetProfilePath(profileName);
        var metadata = LoadMetadata(profilePath);
        if (metadata is null)
        {
            return null;
        }

        var kind = ParseKind(metadata.Kind ?? metadata.Mode);
        if (kind != ProfileKind.ApiKey)
        {
            return null;
        }

        var secretPath = Path.Combine(profilePath, SecretFileName);
        var apiKey = File.Exists(secretPath)
            ? _secretStore.LoadSecret(secretPath)
            : TryReadApiKeyFromAuthJson(Path.Combine(profilePath, AuthSnapshotFileName)) ?? string.Empty;

        var useOpenAiThreadView = metadata.UseOpenAiThreadView
            ?? (!string.IsNullOrWhiteSpace(metadata.Provider)
                && !string.IsNullOrWhiteSpace(metadata.ModelProvider)
                && !string.Equals(metadata.Provider, metadata.ModelProvider, StringComparison.OrdinalIgnoreCase));

        return new ApiProfileSpec
        {
            Name = metadata.Name ?? profileName,
            Provider = metadata.Provider ?? metadata.ModelProvider ?? "crs",
            BaseUrl = metadata.BaseUrl ?? "https://api.funai.vip",
            Model = metadata.Model ?? "gpt-5.4",
            ReasoningEffort = metadata.ReasoningEffort ?? "xhigh",
            WireApi = metadata.WireApi ?? "responses",
            RequiresOpenAiAuth = metadata.RequiresOpenAiAuth ?? true,
            ModelAutoCompactTokenLimit = metadata.ModelAutoCompactTokenLimit ?? 256000,
            ApiKey = apiKey,
            UseOpenAiThreadView = metadata.UseOpenAiThreadView ?? useOpenAiThreadView
        };
    }

    public void SaveChatGptSnapshot(string profileName, string configText, string authText, LiveAuthIdentity? identity = null, bool isAutoCaptured = false)
    {
        EnsureLayout();
        var sanitized = PathHelpers.SanitizeProfileName(profileName);
        var profilePath = GetProfilePath(sanitized);
        PathHelpers.EnsureDirectory(profilePath);

        var resolvedIdentity = identity ?? _liveAuthInspector.InspectChatGptSnapshot(authText);
        var metadata = new StoredProfileMetadata
        {
            Name = sanitized,
            Kind = "chatgpt",
            Email = resolvedIdentity?.Email,
            AccountId = resolvedIdentity?.AccountId,
            IdentityFingerprint = resolvedIdentity?.IdentityFingerprint,
            IsAutoCaptured = isAutoCaptured,
            CreatedAt = ReadExistingCreatedAt(profilePath),
            UpdatedAt = DateTimeOffset.UtcNow
        };

        TextFileService.WriteUtf8NoBom(
            Path.Combine(profilePath, ConfigSnapshotFileName),
            TomlOverlayService.ExtractManagedOverlay(configText));
        _secretStore.SaveSecret(Path.Combine(profilePath, ProtectedAuthSnapshotFileName), authText);
        var legacyAuthPath = Path.Combine(profilePath, AuthSnapshotFileName);
        if (File.Exists(legacyAuthPath))
        {
            File.Delete(legacyAuthPath);
        }
        WriteMetadata(profilePath, metadata);
    }

    public void SaveApiProfile(ApiProfileSpec spec)
    {
        EnsureLayout();
        var sanitized = PathHelpers.SanitizeProfileName(spec.Name);
        var profilePath = GetProfilePath(sanitized);
        PathHelpers.EnsureDirectory(profilePath);

        var identity = _liveAuthInspector.CreateApiIdentity(spec);
        var metadata = new StoredProfileMetadata
        {
            Name = sanitized,
            Kind = "apikey",
            Provider = spec.GetDisplayProvider(),
            ModelProvider = spec.GetEffectiveProviderId(),
            BaseUrl = spec.BaseUrl,
            ApiHost = identity.ApiHost,
            Model = spec.Model,
            ReasoningEffort = spec.ReasoningEffort,
            WireApi = spec.WireApi,
            RequiresOpenAiAuth = spec.RequiresOpenAiAuth,
            ModelAutoCompactTokenLimit = spec.ModelAutoCompactTokenLimit,
            UseOpenAiThreadView = spec.UseOpenAiThreadView,
            IdentityFingerprint = identity.IdentityFingerprint,
            CreatedAt = ReadExistingCreatedAt(profilePath),
            UpdatedAt = DateTimeOffset.UtcNow
        };

        WriteMetadata(profilePath, metadata);
        _secretStore.SaveSecret(Path.Combine(profilePath, SecretFileName), spec.ApiKey);
    }

    public void DeleteProfile(string profileName)
    {
        var path = GetProfilePath(profileName);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    public (string ConfigText, string AuthText, ProfileKind Kind) LoadProfileFilesForSwitch(string profileName, string currentConfigText)
    {
        var profilePath = GetProfilePath(profileName);
        var metadata = LoadMetadata(profilePath) ?? throw new InvalidOperationException($"Profile '{profileName}' does not exist.");
        var kind = ParseKind(metadata.Kind ?? metadata.Mode);
        if (kind == ProfileKind.ChatGptSnapshot)
        {
            var configPath = Path.Combine(profilePath, ConfigSnapshotFileName);
            var snapshotConfigText = File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;
            var authText = ReadChatGptAuth(profilePath)
                ?? throw new InvalidOperationException($"ChatGPT profile '{profileName}' does not contain a usable auth snapshot.");
            var chatGptConfigText = TomlOverlayService.ApplyChatGptOverlay(currentConfigText, snapshotConfigText);
            return (chatGptConfigText, authText, kind);
        }

        var spec = LoadApiProfile(profileName) ?? throw new InvalidOperationException($"API profile '{profileName}' is invalid.");
        var configText = TomlOverlayService.ApplyApiOverlay(currentConfigText, spec);
        var authJson = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["OPENAI_API_KEY"] = spec.ApiKey
        }, JsonDefaults.Pretty);
        return (configText, authJson, kind);
    }

    public CurrentProfileState? ReadCurrentProfileState()
    {
        if (!File.Exists(CurrentProfileStatePath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(CurrentProfileStatePath);
            var dto = JsonSerializer.Deserialize<CurrentProfileStateDto>(json, JsonDefaults.Pretty);
            if (dto is null || string.IsNullOrWhiteSpace(dto.ProfileName))
            {
                return null;
            }

            return new CurrentProfileState
            {
                ProfileName = dto.ProfileName,
                Kind = ParseKind(dto.Kind ?? dto.Mode),
                IdentityFingerprint = dto.IdentityFingerprint,
                Email = dto.Email,
                AccountId = dto.AccountId,
                Provider = dto.Provider,
                BaseUrl = dto.BaseUrl,
                ApiHost = dto.ApiHost,
                SwitchedAt = dto.SwitchedAt ?? DateTimeOffset.UtcNow
            };
        }
        catch
        {
            return null;
        }
    }

    public void WriteCurrentProfileState(ProfileSummary summary)
    {
        EnsureLayout();
        var dto = new CurrentProfileStateDto
        {
            ProfileName = summary.Name,
            Kind = summary.Kind == ProfileKind.ChatGptSnapshot ? "chatgpt" : "apikey",
            IdentityFingerprint = summary.IdentityFingerprint,
            Email = summary.Email,
            AccountId = summary.AccountId,
            Provider = summary.Provider,
            BaseUrl = summary.BaseUrl,
            ApiHost = summary.ApiHost,
            SwitchedAt = DateTimeOffset.UtcNow
        };
        TextFileService.WriteUtf8NoBom(CurrentProfileStatePath, JsonSerializer.Serialize(dto, JsonDefaults.Pretty));
    }

    public void WriteCurrentProfileState(string profileName, ProfileKind kind)
    {
        EnsureLayout();
        var dto = new CurrentProfileStateDto
        {
            ProfileName = profileName,
            Kind = kind == ProfileKind.ChatGptSnapshot ? "chatgpt" : "apikey",
            SwitchedAt = DateTimeOffset.UtcNow
        };
        TextFileService.WriteUtf8NoBom(CurrentProfileStatePath, JsonSerializer.Serialize(dto, JsonDefaults.Pretty));
    }

    public void DeleteCurrentProfileState()
    {
        if (File.Exists(CurrentProfileStatePath))
        {
            File.Delete(CurrentProfileStatePath);
        }
    }

    public string BackupLiveFiles(string configPath, string authPath)
    {
        EnsureLayout();
        var backupName = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var backupPath = Path.Combine(BackupsPath, backupName);
        for (var suffix = 2; Directory.Exists(backupPath); suffix++)
        {
            backupPath = Path.Combine(BackupsPath, $"{backupName}_{suffix}");
        }

        PathHelpers.EnsureDirectory(backupPath);
        File.Copy(configPath, Path.Combine(backupPath, ConfigSnapshotFileName), overwrite: true);
        if (File.Exists(authPath))
        {
            File.Copy(authPath, Path.Combine(backupPath, AuthSnapshotFileName), overwrite: true);
        }

        return backupPath;
    }

    public string GetProfilePath(string profileName)
    {
        return Path.Combine(ProfilesPath, PathHelpers.SanitizeProfileName(profileName));
    }

    public static string MaskSecret(string secret)
    {
        if (secret.Length <= 10)
        {
            return new string('*', secret.Length);
        }

        return $"{secret[..6]}...{secret[^4..]}";
    }

    private ProfileSummary? LoadProfileSummary(string profilePath)
    {
        var metadata = LoadMetadata(profilePath);
        if (metadata is null)
        {
            return null;
        }

        var kind = ParseKind(metadata.Kind ?? metadata.Mode);
        string? apiKey = null;
        var legacyAuthPath = Path.Combine(profilePath, AuthSnapshotFileName);
        var isLegacyPlaintext = false;

        string? email = metadata.Email;
        string? accountId = metadata.AccountId;
        string? identityFingerprint = metadata.IdentityFingerprint;
        string? apiHost = metadata.ApiHost;

        if (kind == ProfileKind.ApiKey)
        {
            var secretPath = Path.Combine(profilePath, SecretFileName);
            if (File.Exists(secretPath))
            {
                apiKey = _secretStore.LoadSecret(secretPath);
            }
            else if (File.Exists(legacyAuthPath))
            {
                apiKey = TryReadApiKeyFromAuthJson(legacyAuthPath);
                isLegacyPlaintext = !string.IsNullOrWhiteSpace(apiKey);
            }

            apiHost ??= TryGetHost(metadata.BaseUrl);
            // v1.1 fingerprints used the forced "openai" ID. Derive identity from
            // the actual saved provider so old profiles still match after switching.
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                identityFingerprint = _liveAuthInspector.CreateApiIdentity(new ApiProfileSpec
                {
                    Provider = metadata.Provider ?? metadata.ModelProvider ?? "crs",
                    BaseUrl = metadata.BaseUrl ?? string.Empty,
                    ApiKey = apiKey
                }).IdentityFingerprint;
            }
        }
        else
        {
            var authText = ReadChatGptAuth(profilePath);
            if (!string.IsNullOrWhiteSpace(authText))
            {
                var legacyIdentity = _liveAuthInspector.InspectChatGptSnapshot(authText);
                email ??= legacyIdentity?.Email;
                accountId ??= legacyIdentity?.AccountId;
                identityFingerprint ??= legacyIdentity?.IdentityFingerprint;
            }
        }

        return new ProfileSummary
        {
            Name = metadata.Name ?? Path.GetFileName(profilePath),
            Kind = kind,
            Email = email,
            AccountId = accountId,
            Provider = metadata.Provider ?? metadata.ModelProvider,
            BaseUrl = metadata.BaseUrl,
            ApiHost = apiHost,
            Model = metadata.Model,
            ApiKeyMask = string.IsNullOrWhiteSpace(apiKey) ? null : MaskSecret(apiKey),
            IdentityFingerprint = identityFingerprint,
            UpdatedAt = metadata.UpdatedAt ?? metadata.CreatedAt ?? Directory.GetLastWriteTimeUtc(profilePath),
            IsAutoCaptured = metadata.IsAutoCaptured ?? false,
            IsLegacyPlaintextApiProfile = isLegacyPlaintext
        };
    }

    private static string CreateChatGptBaseName(LiveAuthIdentity identity)
    {
        if (!string.IsNullOrWhiteSpace(identity.Email))
        {
            return $"chatgpt-{identity.Email.Trim().ToLowerInvariant()}";
        }

        if (!string.IsNullOrWhiteSpace(identity.AccountId))
        {
            var trimmed = identity.AccountId.Trim();
            return $"chatgpt-{trimmed[..Math.Min(8, trimmed.Length)]}";
        }

        return "chatgpt";
    }

    private static string CreateApiBaseName(LiveAuthIdentity identity)
    {
        var provider = string.IsNullOrWhiteSpace(identity.Provider) ? "provider" : identity.Provider.Trim().ToLowerInvariant();
        var host = string.IsNullOrWhiteSpace(identity.ApiHost) ? "host" : identity.ApiHost.Trim().ToLowerInvariant();
        return $"api-{provider}-{host}";
    }

    private static string? TryReadApiKeyFromAuthJson(string authPath)
    {
        if (!File.Exists(authPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(authPath));
            if (document.RootElement.TryGetProperty("OPENAI_API_KEY", out var apiKeyElement))
            {
                return apiKeyElement.GetString();
            }
        }
        catch
        {
        }

        return null;
    }

    private string? ReadChatGptAuth(string profilePath)
    {
        var protectedPath = Path.Combine(profilePath, ProtectedAuthSnapshotFileName);
        if (File.Exists(protectedPath))
        {
            try
            {
                return _secretStore.LoadSecret(protectedPath);
            }
            catch
            {
                return null;
            }
        }

        var legacyPath = Path.Combine(profilePath, AuthSnapshotFileName);
        if (!File.Exists(legacyPath))
        {
            return null;
        }

        try
        {
            var authText = File.ReadAllText(legacyPath);
            _secretStore.SaveSecret(protectedPath, authText);
            try
            {
                File.Delete(legacyPath);
            }
            catch
            {
            }

            return authText;
        }
        catch
        {
            return null;
        }
    }

    private StoredProfileMetadata? LoadMetadata(string profilePath)
    {
        var metadataPath = Path.Combine(profilePath, MetadataFileName);
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StoredProfileMetadata>(File.ReadAllText(metadataPath), JsonDefaults.Pretty);
        }
        catch
        {
            return null;
        }
    }

    private void WriteMetadata(string profilePath, StoredProfileMetadata metadata)
    {
        TextFileService.WriteUtf8NoBom(
            Path.Combine(profilePath, MetadataFileName),
            JsonSerializer.Serialize(metadata, JsonDefaults.Pretty));
    }

    private static DateTimeOffset ReadExistingCreatedAt(string profilePath)
    {
        var metadataPath = Path.Combine(profilePath, MetadataFileName);
        if (!File.Exists(metadataPath))
        {
            return DateTimeOffset.UtcNow;
        }

        try
        {
            var existing = JsonSerializer.Deserialize<StoredProfileMetadata>(File.ReadAllText(metadataPath), JsonDefaults.Pretty);
            return existing?.CreatedAt ?? DateTimeOffset.UtcNow;
        }
        catch
        {
            return DateTimeOffset.UtcNow;
        }
    }

    private static ProfileKind ParseKind(string? raw)
    {
        return raw?.ToLowerInvariant() switch
        {
            "chatgpt" => ProfileKind.ChatGptSnapshot,
            "apikey" => ProfileKind.ApiKey,
            _ => ProfileKind.ChatGptSnapshot
        };
    }

    private static string? TryGetHost(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return Uri.TryCreate(raw, UriKind.Absolute, out var uri) ? uri.Host : raw.Trim();
    }

    private sealed class StoredProfileMetadata
    {
        public string? Name { get; set; }
        public string? Kind { get; set; }
        public string? Mode { get; set; }
        public string? Email { get; set; }
        public string? AccountId { get; set; }
        public string? Provider { get; set; }
        public string? ModelProvider { get; set; }
        public string? BaseUrl { get; set; }
        public string? ApiHost { get; set; }
        public string? Model { get; set; }
        public string? ReasoningEffort { get; set; }
        public string? WireApi { get; set; }
        public bool? RequiresOpenAiAuth { get; set; }
        public bool? DisableResponseStorage { get; set; }
        public int? ModelAutoCompactTokenLimit { get; set; }
        public bool? UseOpenAiThreadView { get; set; }
        public string? IdentityFingerprint { get; set; }
        public bool? IsAutoCaptured { get; set; }
        public DateTimeOffset? CreatedAt { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
    }

    private sealed class CurrentProfileStateDto
    {
        public string? ProfileName { get; set; }
        public string? Kind { get; set; }
        public string? Mode { get; set; }
        public string? IdentityFingerprint { get; set; }
        public string? Email { get; set; }
        public string? AccountId { get; set; }
        public string? Provider { get; set; }
        public string? BaseUrl { get; set; }
        public string? ApiHost { get; set; }
        public DateTimeOffset? SwitchedAt { get; set; }
    }
}
