using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Anima.Launcher.Core;

public sealed record ComfyLaunchOptions(string VramMode = "", double? ReserveVram = null,
    bool DisableSmartMemory = false, string Precision = "", string Attention = "", string Manager = "");
public sealed record ComfyArgumentComparison(bool RootMatches, bool Matches, string Detail);
public sealed class ComfyArgumentsMismatchException(string detail) : InvalidOperationException(detail);

/// <summary>One argument contract for settings, process creation and live-process reuse.</summary>
public static class ComfyLaunchArguments
{
    public static readonly string[] VramModes = ["", "--lowvram", "--novram", "--highvram", "--gpu-only", "--cpu"];
    public static readonly string[] Precisions = ["", "--force-fp32", "--force-fp16"];
    public static readonly string[] Attentions = ["", "--use-pytorch-cross-attention", "--use-split-cross-attention", "--use-quad-cross-attention", "--use-sage-attention", "--use-flash-attention", "--use-ck-attention"];
    public static readonly string[] Managers = ["", "--enable-manager", "--enable-manager-legacy-ui"];
    private static readonly HashSet<string> CommonFlags = VramModes.Concat(Precisions).Concat(Attentions).Concat(Managers)
        .Where(value => value.Length > 0).Append("--disable-smart-memory").ToHashSet(StringComparer.Ordinal);
    private static readonly HashSet<string> Reserved = new(["--listen", "--port", "--base-directory", "--models-directory", "--output-directory", "--input-directory", "--temp-directory", "--user-directory", "--extra-model-paths-config", "--windows-standalone-build", "--disable-auto-launch", "--auto-launch", "--help", "-h"], StringComparer.Ordinal);
    private sealed record Argument(string Name, string[] Values);

    public static List<string> Build(LauncherConfig config)
    {
        ValidateForRoot(config.ComfyArguments, config.ComfyRoot);
        var result = new List<string> { Path.Combine(config.ComfyRoot, "main.py"), "--listen", "127.0.0.1", "--port", config.ComfyPort.ToString(CultureInfo.InvariantCulture), "--disable-auto-launch" };
        if (config.Python.Contains("python_embed", StringComparison.OrdinalIgnoreCase)) result.Add("--windows-standalone-build");
        result.AddRange(config.ComfyArguments);
        return result;
    }

    public static bool ManagerEnabled(IEnumerable<string> arguments) => Groups(arguments).Any(argument => argument.Name is "--enable-manager" or "--enable-manager-legacy-ui");

    public static bool Equivalent(IEnumerable<string> left, IEnumerable<string> right) => Canonical(left).SequenceEqual(Canonical(right), StringComparer.Ordinal);

    /// <summary>Read the installed parser without importing ComfyUI or initializing CUDA/plugins.</summary>
    public static HashSet<string>? SupportedOptions(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        var parser = Path.Combine(root, "comfy", "cli_args.py");
        if (!File.Exists(parser)) return null;
        var source = File.ReadAllText(parser);
        var supported = Regex.Matches(source, """\.add_argument\s*\(\s*["'](--[\w-]+)["']""")
            .Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        // An unrecognized/custom parser must not be mistaken for a parser supporting no options.
        return supported.Count == 0 ? null : supported;
    }

    public static void ValidateForRoot(IEnumerable<string> arguments, string root)
    {
        var tokens = arguments.ToArray();
        Validate(tokens);
        var supported = SupportedOptions(root);
        if (supported is null) return;
        var unsupported = Groups(tokens).Where(argument => (CommonFlags.Contains(argument.Name) || argument.Name == "--reserve-vram") && !supported.Contains(argument.Name))
            .Select(argument => argument.Name).Distinct().ToArray();
        if (unsupported.Length > 0) throw new InvalidDataException("所选 ComfyUI 版本不支持这些启动选项：" + string.Join("、", unsupported) + "。请取消对应选项，或更新该环境后再启用。");
    }

    public static void Validate(IEnumerable<string> arguments)
    {
        var groups = Groups(arguments);
        foreach (var argument in groups)
        {
            if (Reserved.Contains(argument.Name)) throw new InvalidDataException($"参数 {argument.Name} 由启动器管理，不能在额外参数中覆盖。端口请在服务端口中设置。");
            if (CommonFlags.Contains(argument.Name) && argument.Values.Length != 0)
                throw new InvalidDataException($"参数 {argument.Name} 不接受值。");
            if (argument.Name == "--reserve-vram" && (argument.Values.Length != 1 || !double.TryParse(argument.Values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value < 0))
                throw new InvalidDataException("预留显存必须是大于或等于 0 的有限数字，单位 GB。");
        }
        foreach (var duplicate in groups.GroupBy(argument => argument.Name).Where(group => group.Count() > 1 && (CommonFlags.Contains(group.Key) || group.Key == "--reserve-vram")))
            throw new InvalidDataException($"参数 {duplicate.Key} 重复；请在常用选项或高级参数中保留一处。");
        foreach (var (options, label) in new[] { (VramModes, "显存模式"), (Precisions, "全局精度"), (Attentions, "注意力算法") })
            if (groups.Count(argument => options.Contains(argument.Name)) > 1) throw new InvalidDataException($"{label}选项互斥，请只选择一种。");
        if (groups.Any(argument => argument.Name == "--disable-manager-ui") && groups.Any(argument => argument.Name == "--enable-manager-legacy-ui"))
            throw new InvalidDataException("禁用 Manager 界面与启用旧版 Manager 界面不能同时设置。");
    }

    public static (ComfyLaunchOptions Options, List<string> Advanced) ReadOptions(IEnumerable<string> arguments)
    {
        var groups = Groups(arguments);
        string Pick(string[] options) => groups.FirstOrDefault(argument => options.Contains(argument.Name))?.Name ?? "";
        var reserve = groups.FirstOrDefault(argument => argument.Name == "--reserve-vram");
        double? value = reserve?.Values.Length == 1 && double.TryParse(reserve.Values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) && number >= 0 ? number : null;
        var manager = groups.Any(argument => argument.Name == "--enable-manager-legacy-ui") ? "--enable-manager-legacy-ui" : Pick(Managers);
        var options = new ComfyLaunchOptions(Pick(VramModes), value, groups.Any(argument => argument.Name == "--disable-smart-memory"), Pick(Precisions), Pick(Attentions), manager);
        var advanced = groups.Where(argument => !CommonFlags.Contains(argument.Name) && !(argument.Name == "--reserve-vram" && value is not null)).SelectMany(Tokens).ToList();
        return (options, advanced);
    }

    public static List<string> WithOptions(ComfyLaunchOptions options, IEnumerable<string> advanced, bool validate = true)
    {
        var result = new List<string>();
        foreach (var (value, allowed) in new[] { (options.VramMode, VramModes), (options.Precision, Precisions), (options.Attention, Attentions), (options.Manager, Managers) })
        {
            if (!allowed.Contains(value)) throw new InvalidDataException("启动参数选项无效。");
            if (value.Length > 0) result.Add(value);
        }
        if (options.ReserveVram is not null) { result.Add("--reserve-vram"); result.Add(options.ReserveVram.Value.ToString("G", CultureInfo.InvariantCulture)); }
        if (options.DisableSmartMemory) result.Add("--disable-smart-memory");
        result.AddRange(advanced);
        if (validate) Validate(result);
        return result;
    }

    public static ComfyArgumentComparison Compare(JsonElement stats, LauncherConfig config)
    {
        if (!ServiceManager.SameRoot(stats, config.ComfyRoot)) return new(false, false, "当前端口的 ComfyUI 与所选目录不一致。");
        try
        {
            var actual = stats.GetProperty("system").GetProperty("argv").EnumerateArray()
                .Select(value => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new InvalidDataException("运行参数不是字符串。")).ToArray();
            var expected = Build(config).Skip(1).ToArray();
            if (Equivalent(actual.Skip(1), expected)) return new(true, true, "");
            return new(true, false, "ComfyUI 正在使用不同的启动参数，需要重启后生效。启动器没有停止现有进程；本程序启动的服务可在服务页停止后重启，外部服务请自行停止后重试。\n期望：" + Format(expected) + "\n实际：" + Format(actual.Skip(1)));
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or KeyNotFoundException)
        { return new(true, false, "无法核对运行中的 ComfyUI 启动参数，已拒绝复用；请停止对应服务后重试。" + ex.Message); }
    }

    private static string[] Canonical(IEnumerable<string> arguments)
    {
        // Browser-opening flags only act during initial startup, so they do not change the reused server's runtime.
        var groups = Groups(arguments).Where(argument => argument.Name is not ("--disable-auto-launch" or "--auto-launch")).ToList();
        if (!groups.Any(argument => argument.Name == "--listen")) groups.Add(new("--listen", ["127.0.0.1"]));
        if (!groups.Any(argument => argument.Name == "--port")) groups.Add(new("--port", ["8188"]));
        if (groups.Any(argument => argument.Name == "--enable-manager-legacy-ui") && !groups.Any(argument => argument.Name == "--enable-manager")) groups.Add(new("--enable-manager", []));
        bool Known(Argument argument) => CommonFlags.Contains(argument.Name) || argument.Name is "--reserve-vram" or "--listen" or "--port" or "--windows-standalone-build";
        string Key(Argument argument)
        {
            var values = argument.Values;
            if (argument.Name is "--reserve-vram" or "--port" && values.Length == 1 && double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)) values = [number.ToString("G", CultureInfo.InvariantCulture)];
            return JsonSerializer.Serialize(new { argument.Name, Values = values });
        }
        // Unknown options may repeat or consume multiple values; keep their ordering and values intact.
        return groups.Where(Known).Select(Key).Order(StringComparer.Ordinal).Concat(groups.Where(argument => !Known(argument)).Select(Key)).ToArray();
    }

    private static List<Argument> Groups(IEnumerable<string> arguments)
    {
        var result = new List<Argument>();
        string? name = null;
        var values = new List<string>();
        foreach (var token in arguments)
        {
            if (string.IsNullOrEmpty(token) || token.Any(c => c is '\0' or '\r' or '\n')) throw new InvalidDataException("启动参数不能包含空值或控制字符。");
            if (token.StartsWith("--", StringComparison.Ordinal) || token == "-h")
            {
                if (name is not null) result.Add(new(name, values.ToArray()));
                var parts = token.Split('=', 2);
                if (parts[0].Length <= 2 || parts[0].Any(char.IsWhiteSpace)) throw new InvalidDataException("启动参数名称无效：" + parts[0]);
                name = parts[0]; values.Clear();
                if (parts.Length == 2) values.Add(parts[1]);
            }
            else
            {
                if (name is null) throw new InvalidDataException("额外参数应以 --参数名 开始：" + token);
                values.Add(token);
            }
        }
        if (name is not null) result.Add(new(name, values.ToArray()));
        return result;
    }

    private static IEnumerable<string> Tokens(Argument argument)
    {
        // Values starting with a flag must retain equals syntax when the UI rewrites text.
        if (argument.Values.Length > 0 && (argument.Values[0].Length == 0 || argument.Values[0].StartsWith("--", StringComparison.Ordinal) || argument.Values[0] == "-h"))
            return new[] { argument.Name + "=" + argument.Values[0] }.Concat(argument.Values.Skip(1));
        return new[] { argument.Name }.Concat(argument.Values);
    }

    /// <summary>Parse Windows-style double quotes without executing a shell.</summary>
    public static List<string> Parse(string text)
    {
        var result = new List<string>();
        var token = new StringBuilder();
        var quoted = false;
        var started = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\')
            {
                var start = i;
                while (i < text.Length && text[i] == '\\') i++;
                var count = i - start;
                if (i < text.Length && text[i] == '"')
                {
                    token.Append('\\', count / 2);
                    if (count % 2 == 1) token.Append('"'); else quoted = !quoted;
                }
                else { token.Append('\\', count); i--; }
                started = true;
            }
            else if (c == '"') { quoted = !quoted; started = true; }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (started) { result.Add(token.ToString()); token.Clear(); started = false; }
            }
            else { token.Append(c); started = true; }
        }
        if (quoted) throw new InvalidDataException("启动参数的双引号未闭合。");
        if (started) result.Add(token.ToString());
        return result;
    }

    public static string Format(IEnumerable<string> arguments) => string.Join(" ", arguments.Select(Quote));
    private static string Quote(string value)
    {
        if (value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); slashes = 0;
            result.Append(c);
        }
        result.Append('\\', slashes * 2); result.Append('"');
        return result.ToString();
    }
}
