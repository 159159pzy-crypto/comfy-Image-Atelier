using System.Diagnostics;

namespace Anima.Launcher.Core;

/// <summary>Opt-in direct networking for reproducible acceptance runs, without changing system settings.</summary>
public static class NetworkPolicy
{
    public static bool Direct => Environment.GetEnvironmentVariable("ANIMA_LAUNCHER_DIRECT_NETWORK") == "1";

    public static HttpClientHandler HttpHandler(bool allowAutoRedirect = false) => new()
    {
        UseProxy = !Direct,
        AllowAutoRedirect = allowAutoRedirect,
    };

    public static void Apply(ProcessStartInfo info)
    {
        if (!Direct) return;
        foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy", "PIP_PROXY" })
            info.Environment[name] = "";
        info.Environment["NO_PROXY"] = "*";
        info.Environment["no_proxy"] = "*";
        info.Environment["PIP_CONFIG_FILE"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        info.Environment["PIP_EXTRA_INDEX_URL"] = "";
        var count = info.Environment.TryGetValue("GIT_CONFIG_COUNT", out var text) && int.TryParse(text, out var configured) ? configured : 0;
        info.Environment["GIT_CONFIG_KEY_" + count] = "http.proxy";
        info.Environment["GIT_CONFIG_VALUE_" + count] = "";
        info.Environment["GIT_CONFIG_COUNT"] = (count + 1).ToString();
    }
}
