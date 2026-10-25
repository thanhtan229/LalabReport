using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace LalabAutoReport.Server.Health;

public record ServerReadinessResult(
    bool IsReady,
    int? SchemaVersion,
    Dictionary<string, string> Components
);

public class ServerHealthDto
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("buildId")]
    public string BuildId { get; set; } = string.Empty;

    [JsonPropertyName("assemblyVersion")]
    public string AssemblyVersion { get; set; } = string.Empty;

    [JsonPropertyName("schemaVersion")]
    public int? SchemaVersion { get; set; }

    [JsonPropertyName("startedAtUtc")]
    public string StartedAtUtc { get; set; } = string.Empty;

    [JsonPropertyName("cloud")]
    public string Cloud { get; set; } = "disabled";

    [JsonPropertyName("autoScan")]
    public string AutoScan { get; set; } = "disabled";

    [JsonPropertyName("components")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Components { get; set; }
}

public class ServerHealthInfoProvider
{
    private readonly DateTimeOffset _startedAtUtc;
    private readonly string _buildId;
    private readonly string _assemblyVersion;
    private readonly Func<int>? _schemaVersionProvider;
    private readonly Func<bool>? _isReadyProvider;
    private readonly Func<Dictionary<string, string>>? _componentsProvider;
    private readonly Func<Task<ServerReadinessResult>>? _readinessEvaluatorAsync;

    public string BuildId => _buildId;
    public string AssemblyVersion => _assemblyVersion;
    public bool IsReady => _isReadyProvider?.Invoke() ?? false;

    public ServerHealthInfoProvider(
        DateTimeOffset? startedAtUtc = null,
        Func<int>? schemaVersionProvider = null,
        Func<bool>? isReadyProvider = null,
        Func<Dictionary<string, string>>? componentsProvider = null,
        Func<Task<ServerReadinessResult>>? readinessEvaluatorAsync = null)
    {
        _startedAtUtc = startedAtUtc ?? DateTimeOffset.UtcNow;
        _schemaVersionProvider = schemaVersionProvider;
        _isReadyProvider = isReadyProvider;
        _componentsProvider = componentsProvider;
        _readinessEvaluatorAsync = readinessEvaluatorAsync;

        var assembly = typeof(ServerHealthInfoProvider).Assembly;
        var infoVersionAttr = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var asmVer = assembly.GetName().Version?.ToString(3) ?? "2.0.0";

        _assemblyVersion = asmVer;
        // CI commit injection via git SHA into AssemblyInformationalVersion is future work (Phase 2/5).
        // BuildId currently composes AssemblyInformationalVersion and AssemblyVersion.
        _buildId = !string.IsNullOrWhiteSpace(infoVersionAttr) ? $"{infoVersionAttr}+{asmVer}" : asmVer;
    }

    public ServerHealthInfoProvider(
        string? dataDir,
        DateTimeOffset? startedAtUtc = null,
        Func<int>? schemaVersionProvider = null,
        Func<bool>? isReadyProvider = null,
        Func<Dictionary<string, string>>? componentsProvider = null,
        Func<Task<ServerReadinessResult>>? readinessEvaluatorAsync = null)
        : this(startedAtUtc, schemaVersionProvider, isReadyProvider, componentsProvider, readinessEvaluatorAsync)
    {
    }

    public ServerHealthDto GetLiveHealth()
    {
        return new ServerHealthDto
        {
            Status = "Live",
            BuildId = _buildId,
            AssemblyVersion = _assemblyVersion,
            SchemaVersion = _schemaVersionProvider?.Invoke() ?? 0,
            StartedAtUtc = _startedAtUtc.ToString("o"),
            Cloud = "disabled",
            AutoScan = "disabled"
        };
    }

    public async Task<(bool IsReady, ServerHealthDto Health)> GetReadyHealthAsync()
    {
        if (_readinessEvaluatorAsync != null)
        {
            var result = await _readinessEvaluatorAsync();
            var dto = new ServerHealthDto
            {
                Status = result.IsReady ? "Ready" : "NotReady",
                BuildId = _buildId,
                AssemblyVersion = _assemblyVersion,
                SchemaVersion = result.SchemaVersion ?? 0,
                StartedAtUtc = _startedAtUtc.ToString("o"),
                Cloud = "disabled",
                AutoScan = "disabled",
                Components = result.Components
            };
            return (result.IsReady, dto);
        }

        bool ready = IsReady;
        var defaultDto = new ServerHealthDto
        {
            Status = ready ? "Ready" : "NotReady",
            BuildId = _buildId,
            AssemblyVersion = _assemblyVersion,
            SchemaVersion = _schemaVersionProvider?.Invoke() ?? 0,
            StartedAtUtc = _startedAtUtc.ToString("o"),
            Cloud = "disabled",
            AutoScan = "disabled",
            Components = _componentsProvider?.Invoke()
        };
        return (ready, defaultDto);
    }

    public ServerHealthDto GetReadyHealth()
    {
        var task = GetReadyHealthAsync();
        return task.GetAwaiter().GetResult().Health;
    }
}
