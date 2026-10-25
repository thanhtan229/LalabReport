using System.Collections.Generic;

namespace LalabAutoReport.Infrastructure.Web;

public class MobileWebServerOptions
{
    public bool LoopbackOnly { get; set; }
    public IReadOnlySet<string>? EnabledWriteCapabilities { get; set; }
    public bool ReadOnlyMode
    {
        get => EnabledWriteCapabilities != null && EnabledWriteCapabilities.Count == 0;
        set
        {
            if (value)
            {
                EnabledWriteCapabilities = new HashSet<string>();
            }
            else
            {
                EnabledWriteCapabilities = null;
            }
        }
    }
    public int Port { get; set; }
}
