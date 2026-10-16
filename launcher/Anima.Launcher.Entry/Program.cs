using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(uint processId);

    [STAThread]
    private static int Main(string[] args)
    {
        var commandLine = args.Contains("--version") || args.Contains("--diagnose");
        if (commandLine) AttachConsole(0xFFFFFFFF);
        try
        {
            // WinUI activation and PRI resources require the original executable
            // to remain beside its Windows App SDK binaries.
            var executable = Path.Combine(AppContext.BaseDirectory, "AnimaRandomStudio.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("启动器运行文件缺失，请重新部署根目录入口。", executable);
            var info = new ProcessStartInfo(executable)
            {
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = commandLine,
                RedirectStandardError = commandLine,
                CreateNoWindow = commandLine,
            };
            foreach (var argument in args) info.ArgumentList.Add(argument);
            using var process = Process.Start(info) ?? throw new IOException("无法启动 Anima Random Studio。");
            if (!commandLine) return 0;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Console.Out.Write(output.GetAwaiter().GetResult());
            Console.Error.Write(error.GetAwaiter().GetResult());
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            if (commandLine) Console.Error.WriteLine(ex.Message);
            else MessageBoxW(IntPtr.Zero, ex.Message, "Anima Random Studio", 0x10);
            return 1;
        }
    }
}
