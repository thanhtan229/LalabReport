using System;
using System.Reflection;
using System.Text.Json.Serialization;

namespace LalabAutoReport.Server.Health;

public class ServerHealthDto
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("buildId")]
    public string BuildId { get; set; } = string.Empty;

    [JsonPropertyName("assemblyVersion")]
    public string AssemblyVersion { get; set; } = string.Empty;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("dataDir")]
    public string DataDir { get; set; } = string.Empty;

    [JsonPropertyName("startedAtUtc")]
    public string StartedAtUtc { get; set; } = string.Empty;

    [JsonPropertyName("cloud")]
    public string Cloud { get; set; } = "disabled";

    [JsonPropertyName("autoScan")]
    public string AutoScan { get; set; } = "disabled";
}

public class ServerHealthInfoProvider
{
    private readonly string _dataDir;
    private readonly DateTimeOffset _startedAtUtc;
    private readonly string _buildId;
    private readonly string _assemblyVersion;
    private readonly Func<int>? _schemaVersionProvider;
    private readonly Func<bool>? _isReadyProvider;

    public string BuildId => _buildId;
    public string AssemblyVersion => _assemblyVersion;
    public bool IsReady => _isReadyProvider?.Invoke() ?? false;

    public ServerHealthInfoProvider(
        string dataDir,
        DateTimeOffset? startedAtUtc = null,
        Func<int>? schemaVersionProvider = null,
        Func<bool>? isReadyProvider = null)
    {
        _dataDir = dataDir;
        _startedAtUtc = startedAtUtc ?? DateTimeOffset.UtcNow;
        _schemaVersionProvider = schemaVersionProvider;
        _isReadyProvider = isReadyProvider;

        var assembly = typeof(ServerHealthInfoProvider).Assembly;
        var infoVersionAttr = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var asmVer = assembly.GetName().Version?.ToString(3) ?? "2.0.0";

        _assemblyVersion = asmVer;
        // CI commit injection via git SHA into AssemblyInformationalVersion is future work (Phase 2/5).
        // BuildId currently composes AssemblyInformationalVersion and AssemblyVersion.
        _buildId = !string.IsNullOrWhiteSpace(infoVersionAttr) ? $"{infoVersionAttr}+{asmVer}" : asmVer;
    }

    public ServerHealthDto GetLiveHealth()
    {
        return new ServerHealthDto
        {
            Status = "Live",
            BuildId = _buildId,
            AssemblyVersion = _assemblyVersion,
            SchemaVersion = _schemaVersionProvider?.Invoke() ?? 0,
            DataDir = _dataDir,
            StartedAtUtc = _startedAtUtc.ToString("o"),
            Cloud = "disabled",
            AutoScan = "disabled"
        };
    }

    public ServerHealthDto GetReadyHealth()
    {
        bool ready = IsReady;
        return new ServerHealthDto
        {
            Status = ready ? "Ready" : "Starting",
            BuildId = _buildId,
            AssemblyVersion = _assemblyVersion,
            SchemaVersion = _schemaVersionProvider?.Invoke() ?? 0,
            DataDir = _dataDir,
            StartedAtUtc = _startedAtUtc.ToString("o"),
            Cloud = "disabled",
            AutoScan = "disabled"
        };
    }
}
