namespace Anima.Launcher.Core;

/// <summary>A verified immutable Git checkout shared by metadata preparation and wheel building.</summary>
public static class DependencySources
{
    public const string Sam2Url = "https://github.com/facebookresearch/sam2";
    public static async Task<string> PrepareAsync(string url, string commit, string cacheRoot, LauncherConfig config,
        CancellationToken ct, Action<string>? log = null, IDictionary<string, string>? environment = null)
    {
        Safety.Https(url); Safety.Commit(commit);
        var identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url + "\n" + commit))).ToLowerInvariant();
        var target = Safety.Under(Path.Combine(cacheRoot, "dependency-sources"), identity);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var lease = await DownloadTargetLease.AcquireAsync(target, ct);
        var git = "git.exe";
        if (environment?.TryGetValue("PATH", out var search) == true)
            git = search.Split(Path.PathSeparator).Select(dir => Path.Combine(dir.Trim('"'), "git.exe")).FirstOrDefault(File.Exists) ?? git;
        else git = Commands.Find("git.exe") ?? git;
        async Task Validate(string directory)
        {
            var env = environment is null ? new Dictionary<string, string>() : new Dictionary<string, string>(environment);
            Installer.ConfigureGitEnvironment(env, "https://github.com/");
            var head = (await Commands.Run(git, ["rev-parse", "HEAD"], directory, null, ct, env, TimeSpan.FromSeconds(30))).Trim();
            var root = (await Commands.Run(git, ["rev-parse", "--show-toplevel"], directory, null, ct, env, TimeSpan.FromSeconds(30))).Trim();
            if (head != commit || !Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("依赖源码缓存的提交或目录身份不匹配：" + directory);
            var changes = await Commands.Run(git, ["status", "--porcelain", "--untracked-files=no"], directory, null, ct, env, TimeSpan.FromSeconds(30));
            if (!string.IsNullOrWhiteSpace(changes)) throw new InvalidDataException("依赖源码缓存有修改，已保留，请重新检查：" + directory);
            if (File.Exists(Path.Combine(directory, ".gitmodules")))
            {
                var modules = await Commands.Run(git, ["submodule", "status", "--recursive"], directory, null, ct, env, TimeSpan.FromSeconds(30));
                if (modules.Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(line => line[0] != ' ')) throw new InvalidDataException("依赖源码子模块不完整或版本不同。");
            }
        }
        if (Directory.Exists(target)) { await Validate(target); return target; }
        var staged = target + ".preparing-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(staged);
        foreach (var source in InstallationSourcePreferences.GitBases(cacheRoot, config))
        {
            ct.ThrowIfCancellationRequested();
            var env = environment is null ? new Dictionary<string, string>() : new Dictionary<string, string>(environment);
            Installer.ConfigureGitEnvironment(env, source);
            NetworkPolicySnapshot.Capture(config).AddEnvironment(env, new Uri(source));
            env["GIT_HTTP_LOW_SPEED_LIMIT"] = "1024"; env["GIT_HTTP_LOW_SPEED_TIME"] = "30";
            try
            {
                log?.Invoke("准备固定依赖源码 · " + url + " @ " + commit + " · " + source);
                if (!Directory.Exists(Path.Combine(staged, ".git"))) await Commands.Run(git, ["init"], staged, log, ct, env);
                await GitFetchRecovery.RunTransfer(git, ["fetch", "--progress", "--depth", "1", url, commit], staged, log, ct, env);
                await Commands.Run(git, ["checkout", "--detach", "FETCH_HEAD"], staged, log, ct, env);
                if (File.Exists(Path.Combine(staged, ".gitmodules"))) await Commands.Run(git, ["submodule", "update", "--init", "--recursive"], staged, log, ct, env);
                await Validate(staged);
                Directory.Move(staged, target);
                if (Safety.Https(url).Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                    InstallationSourcePreferences.RememberGitBase(cacheRoot, config, source, log);
                return target;
            }
            catch (Exception error) when (!ct.IsCancellationRequested && (Installer.IsSourceFailure(error)
                || Safety.Https(url).Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                    && Installer.IsPublicGitMirrorFailure(error, source, config)))
            { log?.Invoke("依赖源码来源不可用：" + DiagnosticRedaction.Text(error.Message)); }
        }
        throw new IOException("无法获取固定提交的依赖源码；已保留暂存目录：" + staged);
    }
}
