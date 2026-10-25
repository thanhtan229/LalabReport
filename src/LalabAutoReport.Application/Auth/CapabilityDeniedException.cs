using System;

namespace LalabAutoReport.ApplicationCore.Auth;

public class CapabilityDeniedException : Exception
{
    public string Capability { get; }
    public string? Role { get; }

    public CapabilityDeniedException(string capability, string? role = null)
        : base($"Actor with role '{role ?? "None"}' lacks required capability '{capability}'.")
    {
        Capability = capability;
        Role = role;
    }
}
