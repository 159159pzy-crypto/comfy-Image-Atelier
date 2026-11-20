using System.Diagnostics;
using System.Net;
using Microsoft.Win32;

namespace Anima.Launcher.Core;

/// <summary>Serializable transport settings; credentials and signed URLs are never persisted here.</summary>
public sealed record NetworkPolicySnapshot(string Mode, string ProxyAddress = "")
{
    public static void ValidateSettings(string mode, string proxy)
    {
        if (mode is not ("system" or "direct" or "custom")) throw new InvalidDataException("网络模式必须是 system、direct 或 custom。");
        if (mode == "custom" && (!Uri.TryCreate(proxy, UriKind.Absolute, out var address) || address.Scheme != "http"
            || address.UserInfo.Length > 0 || address.Query.Length > 0 || address.Fragment.Length > 0 || address.AbsolutePath != "/"))
            throw new InvalidDataException("自定义代理需填写 HTTP 代理地址（如 http://127.0.0.1:7890），不要在地址中保存账号或密码。");
    }

    public static NetworkPolicySnapshot Capture(LauncherConfig? config = null)
    {
        var mode = NetworkPolicy.Direct ? "direct" : config?.NetworkMode
            ?? Environment.GetEnvironmentVariable("ANIMA_LAUNCHER_NETWORK_MODE") ?? "system";
        var proxy = config?.ProxyAddress ?? Environment.GetEnvironmentVariable("ANIMA_LAUNCHER_PROXY_ADDRESS") ?? "";
        ValidateSettings(mode, proxy);
        return new(mode, mode == "custom" ? proxy : "");
    }

    public HttpClientHandler CreateHttpHandler(bool allowAutoRedirect = false)
    {
        ValidateSettings(Mode, ProxyAddress);
        return new() { UseProxy = Mode != "direct", Proxy = Mode == "custom" ? new WebProxy(ProxyAddress) : null,
            AllowAutoRedirect = allowAutoRedirect };
    }

    public void AddEnvironment(IDictionary<string, string> environment, Uri? destination = null)
    {
        environment["ANIMA_LAUNCHER_NETWORK_MODE"] = Mode;
        environment["ANIMA_LAUNCHER_PROXY_ADDRESS"] = ProxyAddress;
        if (destination is not null) environment["ANIMA_LAUNCHER_NETWORK_TARGET"] = destination.GetLeftPart(UriPartial.Path);
    }

    public void Apply(ProcessStartInfo info, Uri? destination = null)
    {
        ValidateSettings(Mode, ProxyAddress);
        if (Mode == "system") RejectPac();
        destination ??= new Uri("https://github.com/");
        string proxy = "";
        if (Mode == "custom") proxy = ProxyAddress;
        else if (Mode == "system")
        {
            var system = HttpClient.DefaultProxy;
            if (!system.IsBypassed(destination))
            {
                var resolved = system.GetProxy(destination);
                if (resolved is not null && resolved != destination)
                {
                    if (resolved.UserInfo.Length > 0) throw new InvalidDataException("系统代理包含内嵌凭据，不能安全传递给安装子进程；请使用无内嵌凭据的 HTTP 代理。");
                    if (resolved.Scheme != "http") throw new InvalidDataException("安装子进程仅支持 HTTP 代理；请在网络设置中选择 HTTP 代理或直连。");
                    proxy = resolved.AbsoluteUri;
                }
            }
        }
        foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy", "PIP_PROXY" }) info.Environment[name] = proxy;
        var bypass = Mode == "direct" || proxy.Length == 0 ? "*" : "localhost,127.0.0.1,::1";
        if (Mode == "system" && proxy.Length > 0 && Environment.GetEnvironmentVariable("NO_PROXY") is { Length: > 0 } existing) bypass += "," + existing;
        info.Environment["NO_PROXY"] = bypass;
        info.Environment["no_proxy"] = bypass;
        info.Environment["PIP_CONFIG_FILE"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        info.Environment["PIP_EXTRA_INDEX_URL"] = "";
        var count = info.Environment.TryGetValue("GIT_CONFIG_COUNT", out var text) && int.TryParse(text, out var configured) ? configured : 0;
        info.Environment["GIT_CONFIG_KEY_" + count] = "http.proxy";
        info.Environment["GIT_CONFIG_VALUE_" + count] = proxy;
        info.Environment["GIT_CONFIG_COUNT"] = (count + 1).ToString();
        foreach (var key in new[] { "ANIMA_LAUNCHER_NETWORK_MODE", "ANIMA_LAUNCHER_PROXY_ADDRESS", "ANIMA_LAUNCHER_NETWORK_TARGET" }) info.Environment.Remove(key);
    }

    public static bool SystemUsesPac()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        return key?.GetValue("AutoConfigURL") is string { Length: > 0 };
    }

    public static void RejectPac()
    {
        if (SystemUsesPac()) throw new InvalidDataException("检测到系统 PAC 自动代理脚本。请在「安装位置 → 高级下载设置」选择直连或填写 HTTP 代理地址，然后重新检查。");
    }
}

/// <summary>Compatibility facade. Direct acceptance runs always override persisted settings.</summary>
public static class NetworkPolicy
{
    public static bool Direct => Environment.GetEnvironmentVariable("ANIMA_LAUNCHER_DIRECT_NETWORK") == "1";
    public static HttpClientHandler HttpHandler(bool allowAutoRedirect = false) => NetworkPolicySnapshot.Capture().CreateHttpHandler(allowAutoRedirect);
    public static void Apply(ProcessStartInfo info)
    {
        string? Get(string key) => info.Environment.TryGetValue(key, out var value) ? value : null;
        var mode = Direct ? "direct" : Get("ANIMA_LAUNCHER_NETWORK_MODE") ?? "system";
        var proxy = Get("ANIMA_LAUNCHER_PROXY_ADDRESS") ?? "";
        var target = Get("ANIMA_LAUNCHER_NETWORK_TARGET");
        // Local diagnostics and service launches have no requested install transport.
        // Leave their inherited OS settings intact when a PAC cannot be translated.
        if (mode == "system" && target is null && Get("ANIMA_LAUNCHER_NETWORK_MODE") is null
            && NetworkPolicySnapshot.SystemUsesPac()) return;
        new NetworkPolicySnapshot(mode, proxy).Apply(info, Uri.TryCreate(target, UriKind.Absolute, out var uri) ? uri : null);
    }
}
