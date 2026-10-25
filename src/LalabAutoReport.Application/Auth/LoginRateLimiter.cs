using System;
using System.Collections.Generic;

namespace LalabAutoReport.ApplicationCore.Auth;

public sealed class LoginRateLimiter
{
    private readonly TimeProvider _timeProvider;
    private readonly object _syncRoot = new();

    private readonly TimeSpan _window = TimeSpan.FromMinutes(10);
    private readonly TimeSpan _maxLockout = TimeSpan.FromMinutes(15);
    private readonly TimeSpan _baseLockout = TimeSpan.FromMinutes(1);

    private const int IpFailureThreshold = 10;
    private const int DeviceFailureThreshold = 5;

    private readonly Dictionary<string, IpTracker> _ipTrackers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DeviceTracker> _deviceTrackers = new(StringComparer.OrdinalIgnoreCase);

    public LoginRateLimiter(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public (bool IsLimited, TimeSpan RetryAfter) CheckRateLimit(string ip, string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(ip))
            ip = "unknown";

        var now = _timeProvider.GetUtcNow();

        lock (_syncRoot)
        {
            // 1. Check IP lockout
            if (_ipTrackers.TryGetValue(ip, out var ipTracker))
            {
                if (ipTracker.LockedUntilUtc > now)
                {
                    return (true, ipTracker.LockedUntilUtc - now);
                }
            }

            // 2. Check Device lockout if deviceId provided
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                string deviceKey = BuildDeviceKey(ip, deviceId);
                if (_deviceTrackers.TryGetValue(deviceKey, out var devTracker))
                {
                    if (devTracker.LockedUntilUtc > now)
                    {
                        return (true, devTracker.LockedUntilUtc - now);
                    }
                }
            }

            return (false, TimeSpan.Zero);
        }
    }

    public void RecordFailure(string ip, string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(ip))
            ip = "unknown";

        var now = _timeProvider.GetUtcNow();

        lock (_syncRoot)
        {
            // 1. Track IP
            if (!_ipTrackers.TryGetValue(ip, out var ipTracker))
            {
                ipTracker = new IpTracker();
                _ipTrackers[ip] = ipTracker;
            }

            ipTracker.CleanOldFailures(now, _window);
            ipTracker.FailureTimestamps.Add(now);

            if (ipTracker.FailureTimestamps.Count >= IpFailureThreshold)
            {
                // Double lockout for consecutive lockouts
                int lockMultiplier = 1 << Math.Min(ipTracker.LockoutCount, 4); // 1, 2, 4, 8, 16 -> cap at 15m
                var duration = TimeSpan.FromSeconds(Math.Min(_baseLockout.TotalSeconds * lockMultiplier, _maxLockout.TotalSeconds));
                ipTracker.LockedUntilUtc = now + duration;
                ipTracker.LockoutCount++;
                ipTracker.FailureTimestamps.Clear();
            }

            // 2. Track IP + deviceId
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                string deviceKey = BuildDeviceKey(ip, deviceId);
                if (!_deviceTrackers.TryGetValue(deviceKey, out var devTracker))
                {
                    devTracker = new DeviceTracker();
                    _deviceTrackers[deviceKey] = devTracker;
                }

                devTracker.CleanOldFailures(now, _window);
                devTracker.FailureTimestamps.Add(now);

                if (devTracker.FailureTimestamps.Count >= DeviceFailureThreshold)
                {
                    devTracker.LockedUntilUtc = now + _baseLockout;
                    devTracker.FailureTimestamps.Clear();
                }
            }
        }
    }

    public void RecordSuccess(string ip, string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(ip))
            ip = "unknown";

        lock (_syncRoot)
        {
            if (_ipTrackers.TryGetValue(ip, out var ipTracker))
            {
                ipTracker.FailureTimestamps.Clear();
                ipTracker.LockoutCount = 0;
                ipTracker.LockedUntilUtc = DateTimeOffset.MinValue;
            }

            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                string deviceKey = BuildDeviceKey(ip, deviceId);
                if (_deviceTrackers.TryGetValue(deviceKey, out var devTracker))
                {
                    devTracker.FailureTimestamps.Clear();
                    devTracker.LockedUntilUtc = DateTimeOffset.MinValue;
                }
            }
        }
    }

    private static string BuildDeviceKey(string ip, string deviceId) => $"{ip}::{deviceId.Trim()}";

    private sealed class IpTracker
    {
        public List<DateTimeOffset> FailureTimestamps { get; } = new();
        public DateTimeOffset LockedUntilUtc { get; set; } = DateTimeOffset.MinValue;
        public int LockoutCount { get; set; }

        public void CleanOldFailures(DateTimeOffset now, TimeSpan window)
        {
            var cutoff = now - window;
            FailureTimestamps.RemoveAll(t => t < cutoff);
        }
    }

    private sealed class DeviceTracker
    {
        public List<DateTimeOffset> FailureTimestamps { get; } = new();
        public DateTimeOffset LockedUntilUtc { get; set; } = DateTimeOffset.MinValue;

        public void CleanOldFailures(DateTimeOffset now, TimeSpan window)
        {
            var cutoff = now - window;
            FailureTimestamps.RemoveAll(t => t < cutoff);
        }
    }
}
