using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Anima.Launcher.Core;

public sealed record LauncherRelease(string Version, string Tag, string Notes, DateTimeOffset PublishedAt,
    string ExeUrl, long Size, string Sha256, string BuildInfoUrl, JsonElement BuildInfo);
public sealed record InstalledLauncher(string Version, string Executable, long Size, string Sha256);
public sealed record FailedLauncher(string Version, string Sha256, string Reason, DateTimeOffset At);
public sealed class LauncherUpdateState
{
    public int ProtocolVersion { get; set; } = 1;
    public InstalledLauncher? Active { get; set; }
    public InstalledLauncher? Previous { get; set; }
    public InstalledLauncher? Downloaded { get; set; }
    public string? PendingTransactionId { get; set; }
    public List<FailedLauncher> Failed { get; set; } = [];
    public List<string> EntryPaths { get; set; } = [];
}
public sealed record LauncherReleaseCache(DateTimeOffset CheckedAt, LauncherRelease? Release);
public sealed record LauncherPackageFile(string Path, long Size, string Sha256);
public sealed record LauncherPackageInfo(int ProtocolVersion, string Version, string PackageFormat,
    string ExecutablePath, string BundleDirectory, JsonElement BuildInfo, LauncherPackageFile[] Files);
public sealed class UpdateTransaction
{
    public int ProtocolVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Token { get; set; } = "";
    public InstalledLauncher Candidate { get; set; } = null!;
    public InstalledLauncher Previous { get; set; } = null!;
    public LauncherConfig PreviousConfig { get; set; } = new();
    public string? PreviousConfigText { get; set; }
    public bool WorkbenchWasRunning { get; set; }
    public OwnedProcess? Parent { get; set; }
    public OwnedProcess? Coordinator { get; set; }
    public OwnedProcess? CandidateProcess { get; set; }
    public string Phase { get; set; } = "prepared";
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed record UpdateReady(string Token, bool Success, string? Error, string AppRoot, int Pid);

/// <summary>Only this store owns launcher binaries and transaction files. User data stays outside it.</summary>
public sealed class LauncherUpdateStore
{
    public string StateDirectory { get; }
    public string Root { get; }
    public string StatePath => Path.Combine(Root, "state.json");
    public string MutexName { get; }
    public LauncherUpdateStore(string stateDirectory)
    {
        StateDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stateDirectory));
        Root = Path.Combine(StateDirectory, "launcher-updates");
        MutexName = "Local\\AnimaLauncherUpdate-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(StateDirectory.ToUpperInvariant())))[..16];
    }
    public LauncherUpdateState Load()
    {
        if (!File.Exists(StatePath)) return new();
        var value = JsonFile.Read<LauncherUpdateState>(StatePath);
        if (value.ProtocolVersion != 1) throw new InvalidDataException("不支持此启动器更新状态版本。");
        return value;
    }
    public void Save(LauncherUpdateState value) => JsonFile.Write(StatePath, value);
    public T Mutate<T>(Func<LauncherUpdateState, T> action)
    {
        using var mutex = new Mutex(false, MutexName);
        bool acquired;
        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(15)); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("更新状态正由另一个启动器处理，请稍后重试。");
        try { var state = Load(); var result = action(state); Save(state); return result; }
        finally { mutex.ReleaseMutex(); }
    }
    public string TransactionPath(string id)
    {
        if (!Regex.IsMatch(id ?? "", "^[a-f0-9]{32}$")) throw new InvalidDataException("更新事务标识无效。");
        return Safety.Under(Root, "transactions/" + id + ".json");
    }
    public string ReceiptPath(string id) => TransactionPath(id) + ".ready";
    public UpdateTransaction ReadTransaction(string id, string? token = null)
    {
        var value = JsonFile.Read<UpdateTransaction>(TransactionPath(id));
        if (value.ProtocolVersion != 1 || value.Id != id || !Regex.IsMatch(value.Token, "^[a-f0-9]{64}$")
            || token is not null && value.Token != token) throw new InvalidDataException("更新事务验证失败。");
        ValidateInstalled(value.Candidate, managed: true);
        ValidateInstalled(value.Previous, managed: false);
        value.PreviousConfig.Validate();
        return value;
    }
    public void WriteTransaction(UpdateTransaction value) => JsonFile.Write(TransactionPath(value.Id), value);
    public string CandidatePath(string version, string sha)
    {
        LauncherUpdates.ParseVersion(version);
        if (!Regex.IsMatch(sha, "^[a-fA-F0-9]{64}$")) throw new InvalidDataException("更新文件缺少 SHA-256。");
        return Safety.Under(Root, version + "-" + sha.ToLowerInvariant()[..16] + "/AnimaRandomStudio.exe");
    }
    public void ValidateInstalled(InstalledLauncher value, bool managed)
    {
        LauncherUpdates.ParseVersion(value.Version);
        if (value.Size <= 0 || !Regex.IsMatch(value.Sha256, "^[a-fA-F0-9]{64}$") || !Path.IsPathFullyQualified(value.Executable)
            || managed && !Path.GetFileName(value.Executable).Equals("AnimaRandomStudio.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("启动器版本记录无效。");
        if (managed && !SamePath(value.Executable, CandidatePath(value.Version, value.Sha256)))
            throw new InvalidDataException("更新程序不在受管版本目录中。");
        RequireNoLinks(Path.GetDirectoryName(value.Executable)!);
        if (File.Exists(value.Executable) && (File.GetAttributes(value.Executable) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("启动器文件不能是链接。");
    }
    public static bool SamePath(string a, string b) => Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar)
        .Equals(Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    public static void RequireNoLinks(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("更新目录不能经过目录链接。");
    }
    public bool InProgress()
    {
        using var mutex = new Mutex(false, MutexName);
        bool acquired;
        try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (acquired) mutex.ReleaseMutex();
        return !acquired || Load().PendingTransactionId is not null;
    }
    public static OwnedProcess Identity(Process process) => new(process.Id, process.StartTime.ToUniversalTime().Ticks,
        process.MainModule?.FileName ?? throw new IOException("无法读取启动器进程身份。"), "Launcher");
    public static bool IsRunning(OwnedProcess? identity)
    {
        if (identity is null) return false;
        try { using var process = Process.GetProcessById(identity.Pid); return !process.HasExited && ServiceManager.SameProcessIdentity(identity, Identity(process)); }
        catch { return false; }
    }
}

public sealed class LauncherUpdates : IDisposable
{
    public const string Repository = "159159pzy-crypto/comfyui-random-image-generation";
    public const string LatestReleaseUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
    public LauncherUpdateStore Store { get; }
    public string CurrentExecutable { get; }
    public string CurrentVersion { get; }
    private readonly HttpClient http;
    private readonly Action<string> log;
    private string CachePath => Path.Combine(Store.Root, "release-cache.json");
    public LauncherUpdates(string stateDir, string currentExe, string currentVersion, Action<string>? log = null, HttpMessageHandler? handler = null)
    {
        Store = new(stateDir); CurrentExecutable = Path.GetFullPath(currentExe); CurrentVersion = currentVersion;
        this.log = log ?? (_ => { });
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AnimaRandomStudio", currentVersion));
    }
    public static Version ParseVersion(string text)
    {
        if (!Regex.IsMatch(text ?? "", "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$") || !Version.TryParse(text, out var value))
            throw new InvalidDataException("启动器版本必须是稳定版三段版本号。");
        return value;
    }
    public LauncherRelease? GetCachedRelease()
    {
        try { return File.Exists(CachePath) ? JsonFile.Read<LauncherReleaseCache>(CachePath).Release : null; } catch { return null; }
    }
    public InstalledLauncher? GetDownloaded()
    {
        var candidate = Store.Load().Downloaded;
        if (candidate is null) return null;
        try { Store.ValidateInstalled(candidate, true); return File.Exists(candidate.Executable) ? candidate : null; } catch { return null; }
    }
    public bool IsFailed(InstalledLauncher candidate) => Store.Load().Failed.Any(x => x.Version == candidate.Version && x.Sha256.Equals(candidate.Sha256, StringComparison.OrdinalIgnoreCase));
    public async Task<LauncherRelease?> CheckAsync(bool force, CancellationToken ct)
    {
        if (!force && File.Exists(CachePath))
        {
            try { var cached = JsonFile.Read<LauncherReleaseCache>(CachePath); if (DateTimeOffset.UtcNow - cached.CheckedAt < TimeSpan.FromHours(24)) return cached.Release; } catch { }
        }
        var json = await GetText(LatestReleaseUrl, 1024 * 1024, ct);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) throw new InvalidDataException("发布记录不是稳定版本。");
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = tag.StartsWith('v') ? tag[1..] : tag;
        if (ParseVersion(version) <= ParseVersion(CurrentVersion))
        {
            JsonFile.Write(CachePath, new LauncherReleaseCache(DateTimeOffset.UtcNow, null));
            return null;
        }
        var assets = root.GetProperty("assets").EnumerateArray().ToArray();
        JsonElement Asset(string name) => assets.SingleOrDefault(a => a.GetProperty("name").GetString() == name) is var result && result.ValueKind == JsonValueKind.Object
            ? result : throw new InvalidDataException("发行版缺少 " + name + "，暂不能在软件内安装。");
        var exe = Asset("AnimaRandomStudio.exe");
        var sums = Asset("SHA256SUMS.txt");
        var build = Asset("build-info.json");
        string Url(JsonElement asset)
        {
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            var uri = Safety.Https(url);
            if (uri.Host != "github.com" || !uri.AbsolutePath.StartsWith("/" + Repository + "/releases/download/" + Uri.EscapeDataString(tag) + "/", StringComparison.Ordinal))
                throw new InvalidDataException("更新资产不属于官方发行版。");
            return url;
        }
        var size = exe.GetProperty("size").GetInt64();
        if (size <= 0 || size > 1024L * 1024 * 1024) throw new InvalidDataException("启动器更新大小无效。");
        var sumText = await GetText(Url(sums), 64 * 1024, ct);
        var sha = ReadChecksum(sumText, "AnimaRandomStudio.exe");
        using var buildDocument = JsonDocument.Parse(await GetText(Url(build), 1024 * 1024, ct));
        ValidateBuildInfo(buildDocument.RootElement, version);
        var result = new LauncherRelease(version, tag, root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
            root.GetProperty("published_at").GetDateTimeOffset(), Url(exe), size, sha, Url(build), buildDocument.RootElement.Clone());
        JsonFile.Write(CachePath, new LauncherReleaseCache(DateTimeOffset.UtcNow, result));
        return result;
    }
    public static string ReadChecksum(string text, string name)
    {
        var matching = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.TrimEnd('\r'))
            .Select(x => Regex.Match(x, "^([a-fA-F0-9]{64})  (.+)$")).Where(x => x.Success && x.Groups[2].Value == name).ToArray();
        if (matching.Length != 1) throw new InvalidDataException("官方校验文件缺少唯一的启动器 SHA-256。");
        return matching[0].Groups[1].Value.ToLowerInvariant();
    }
    public static void ValidateBuildInfo(JsonElement build, string version)
    {
        if (build.ValueKind != JsonValueKind.Object || build.GetProperty("version").GetString() != version
            || build.GetProperty("runtimeIdentifier").GetString() != "win-x64"
            || build.GetProperty("packageFormat").GetString() != "single-exe-v1") throw new InvalidDataException("启动器包元数据与发行版不符。");
    }
    private async Task<string> GetText(string url, int maxBytes, CancellationToken ct)
    {
        var uri = Safety.Https(url);
        for (var i = 0; i < 8; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                uri = response.Headers.Location is { } location ? (location.IsAbsoluteUri ? location : new Uri(uri, location)) : throw new IOException("更新来源缺少重定向地址。");
                if (uri.Scheme != "https" || uri.UserInfo.Length > 0) throw new InvalidDataException("拒绝不安全的更新重定向。");
                continue;
            }
            if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429) throw new IOException("GitHub 检查更新暂时受限，请稍后重试。");
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(30));
            int read;
            while ((read = await stream.ReadAsync(bytes, deadline.Token)) > 0)
            {
                if (buffer.Length + read > maxBytes) throw new InvalidDataException("更新元数据过大。");
                buffer.Write(bytes, 0, read);
            }
            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }
        throw new IOException("更新来源重定向次数过多。");
    }
    public async Task<InstalledLauncher> DownloadAsync(LauncherRelease release, Downloads downloads, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        ValidateRelease(release);
        if (ParseVersion(release.Version) <= ParseVersion(CurrentVersion)) throw new InvalidOperationException("此发行版不比当前启动器更新。");
        var candidate = new InstalledLauncher(release.Version, Store.CandidatePath(release.Version, release.Sha256), release.Size, release.Sha256);
        Store.ValidateInstalled(candidate, true);
        if (File.Exists(candidate.Executable) && !await Downloads.Verify(candidate.Executable, candidate.Size, candidate.Sha256, ct))
            QuarantineInvalidCandidate(candidate);
        await downloads.Fetch("启动器 " + release.Version, release.ExeUrl, candidate.Executable, candidate.Size, candidate.Sha256, progress, ct);
        var package = await InspectPackageAsync(candidate.Executable, ct);
        if (package.Version != release.Version || package.BuildInfo.GetProperty("sourceCommit").GetString() != release.BuildInfo.GetProperty("sourceCommit").GetString()
            || package.BuildInfo.GetProperty("payloadSha256").GetString() != release.BuildInfo.GetProperty("payloadSha256").GetString())
            throw new InvalidDataException("下载的启动器内容与官方构建信息不一致。");
        Store.Mutate(state => { state.Downloaded = candidate; return true; });
        return candidate;
    }
    private void QuarantineInvalidCandidate(InstalledLauncher candidate)
    {
        Store.ValidateInstalled(candidate, true);
        Store.Mutate(state =>
        {
            bool Uses(InstalledLauncher? value) => value is not null && LauncherUpdateStore.SamePath(value.Executable, candidate.Executable);
            var pending = state.PendingTransactionId is { } id ? Store.ReadTransaction(id) : null;
            if (Uses(state.Active) || Uses(state.Previous) || Uses(pending?.Candidate) || Uses(pending?.Previous))
                throw new IOException("此启动器文件仍用于活动版本或更新恢复，已保留原文件；请先完成恢复再重试。");
            // Recheck while holding the state lock: another completed import may
            // have repaired the same cache after the initial asynchronous hash.
            if (Downloads.Verify(candidate.Executable, candidate.Size, candidate.Sha256, default).GetAwaiter().GetResult()) return false;
            if (File.Exists(candidate.Executable))
                File.Move(candidate.Executable, candidate.Executable + ".corrupt-" + Guid.NewGuid().ToString("N"), false);
            if (Uses(state.Downloaded)) state.Downloaded = null;
            return true;
        });
    }
    public static void ValidateRelease(LauncherRelease release)
    {
        ParseVersion(release.Version);
        if (release.Tag != release.Version && release.Tag != "v" + release.Version) throw new InvalidDataException("更新标签与版本不一致。");
        if (release.Size <= 0 || release.Size > 1024L * 1024 * 1024 || !Regex.IsMatch(release.Sha256, "^[a-fA-F0-9]{64}$")) throw new InvalidDataException("更新文件信息无效。");
        foreach (var (url, name) in new[] { (release.ExeUrl, "AnimaRandomStudio.exe"), (release.BuildInfoUrl, "build-info.json") })
        {
            var uri = Safety.Https(url);
            if (uri.Host != "github.com" || uri.Query.Length > 0 || uri.AbsolutePath != "/" + Repository + "/releases/download/" + Uri.EscapeDataString(release.Tag) + "/" + name)
                throw new InvalidDataException("缓存更新地址不属于官方发行版。");
        }
        ValidateBuildInfo(release.BuildInfo, release.Version);
    }
    public async Task<LauncherPackageInfo> InspectPackageAsync(string executable, CancellationToken ct)
    {
        Directory.CreateDirectory(Store.Root);
        var report = Path.Combine(Store.Root, "package-report-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
            foreach (var arg in new[] { "--package-info", "--report", report }) info.ArgumentList.Add(arg);
            info.Environment["ANIMA_LAUNCHER_HOME"] = Store.StateDirectory;
            using var process = Process.Start(info) ?? throw new IOException("无法验证启动器包。");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(2));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch { if (!process.HasExited) process.Kill(); throw; }
            if (process.ExitCode != 0 || !File.Exists(report)) throw new InvalidDataException("启动器包自检失败。");
            var result = JsonFile.Read<LauncherPackageInfo>(report);
            if (result.ProtocolVersion != 1 || result.PackageFormat != "single-exe-v1" || !LauncherUpdateStore.SamePath(result.ExecutablePath, executable))
                throw new InvalidDataException("启动器包验证协议不匹配。");
            ValidateBuildInfo(result.BuildInfo, result.Version);
            return result;
        }
        finally { if (File.Exists(report)) File.Delete(report); }
    }
    public async Task<InstalledLauncher> IdentifyAsync(string executable, string version, CancellationToken ct)
    {
        await using var input = File.OpenRead(executable);
        return new(version, Path.GetFullPath(executable), input.Length, Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant());
    }
    public async Task<InstalledLauncher> ImportAsync(string executable, string version, CancellationToken ct, bool allowRepair = false)
    {
        var source = await IdentifyAsync(executable, version, ct);
        var target = Store.CandidatePath(version, source.Sha256);
        Store.ValidateInstalled(source with { Executable = target }, true);
        if (LauncherUpdateStore.SamePath(executable, target)) return source;
        if (!await Downloads.Verify(target, source.Size, source.Sha256, ct))
        {
            if (File.Exists(target) && !allowRepair) throw new IOException("受管启动器已存在但校验失败，原文件保持不变。");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temporary = target + ".importing";
            await using (var input = File.OpenRead(executable))
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true))
                await input.CopyToAsync(output, ct);
            if (!await Downloads.Verify(temporary, source.Size, source.Sha256, ct)) throw new InvalidDataException("导入启动器时文件校验失败。");
            if (File.Exists(target)) File.Move(target, target + ".corrupt-" + Guid.NewGuid().ToString("N"), false);
            File.Move(temporary, target, false);
        }
        return source with { Executable = target };
    }
    public async Task<UpdateTransaction> PrepareAsync(InstalledLauncher installed, LauncherConfig config, bool workbenchWasRunning, CancellationToken ct)
    {
        Store.ValidateInstalled(installed, true);
        if (!await Downloads.Verify(installed.Executable, installed.Size, installed.Sha256, ct))
        {
            QuarantineInvalidCandidate(installed);
            throw new InvalidDataException("待安装启动器校验失败，异常文件已保留。请重新下载更新后重试。");
        }
        var state = Store.Load();
        if (state.PendingTransactionId is not null) throw new IOException("已有尚未结束的更新事务，请重新打开启动器以恢复。");
        var previous = state.Active ?? await ImportAsync(CurrentExecutable, CurrentVersion, ct);
        if (!await Downloads.Verify(previous.Executable, previous.Size, previous.Sha256, ct)) throw new IOException("旧启动器备份校验失败，暂不能安全更新。");
        if (ParseVersion(installed.Version) <= ParseVersion(previous.Version)) throw new InvalidOperationException("不能覆盖相同或更新的活动版本。");
        var transaction = new UpdateTransaction { Id = Guid.NewGuid().ToString("N"), Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            Candidate = installed, Previous = previous, PreviousConfig = RuntimePolicies.SnapshotConfig(config), WorkbenchWasRunning = workbenchWasRunning,
            PreviousConfigText = File.Exists(Path.Combine(Store.StateDirectory, "launcher.json")) ? File.ReadAllText(Path.Combine(Store.StateDirectory, "launcher.json")) : null,
            Parent = LauncherUpdateStore.Identity(Process.GetCurrentProcess()) };
        Store.Mutate(current =>
        {
            if (current.PendingTransactionId is not null || current.Active is not null && current.Active != previous)
                throw new IOException("活动版本或更新事务已改变，请重新检查后再更新。");
            Store.WriteTransaction(transaction); current.PendingTransactionId = transaction.Id; return true;
        });
        return transaction;
    }
    public bool StartCoordinator(UpdateTransaction transaction)
    {
        var info = ProcessInfo(transaction.Candidate.Executable, "--apply-update", transaction);
        using var process = Process.Start(info) ?? throw new IOException("无法启动更新协调器。");
        for (var i = 0; i < 100; i++)
        {
            if (process.HasExited) throw new IOException("更新协调器启动失败；当前启动器仍然可用。");
            var saved = Store.ReadTransaction(transaction.Id, transaction.Token);
            if (saved.Coordinator?.Pid == process.Id) return true;
            Thread.Sleep(100);
        }
        throw new IOException("更新协调器未及时就绪；请重试打开启动器恢复。");
    }
    public ProcessStartInfo ProcessInfo(string executable, string mode, UpdateTransaction transaction)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (var arg in new[] { mode, transaction.Id, "--update-token", transaction.Token, "--show" }) info.ArgumentList.Add(arg);
        info.Environment["ANIMA_LAUNCHER_HOME"] = Store.StateDirectory;
        return info;
    }
    public void Dispose() => http.Dispose();
}
