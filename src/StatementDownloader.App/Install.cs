using System.Diagnostics;
using System.Security;

namespace StatementDownloader.App;

/// <summary>Registers / removes the agent so it starts at sign-in.</summary>
public static class Install
{
    private const string TaskName = "Statement Downloader";
    private const string LaunchLabel = "io.github.statement-downloader.agent";

    /// <summary>The command that starts this program (handles `dotnet statement-downloader.dll`).</summary>
    public static string[] SelfCommand(params string[] args)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find this program's path.");
        var prefix = Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? new[] { exe, typeof(Install).Assembly.Location }
            : [exe];
        return [.. prefix, .. args];
    }

    public static int Register(string? configPath)
    {
        var args = configPath is null ? new[] { "serve" } : ["serve", "--config", Path.GetFullPath(configPath)];
        var cmd = SelfCommand(args);
        if (OperatingSystem.IsWindows())
        {
            var tr = string.Join(' ', cmd.Select(Quote));
            return Run("schtasks", "/Create", "/TN", TaskName, "/TR", tr, "/SC", "ONLOGON", "/RL", "LIMITED", "/F");
        }
        if (OperatingSystem.IsMacOS())
        {
            var plist = PlistPath();
            Directory.CreateDirectory(Path.GetDirectoryName(plist)!);
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs");
            File.WriteAllText(plist, $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                  <key>Label</key><string>{LaunchLabel}</string>
                  <key>ProgramArguments</key>
                  <array>
                {string.Join("\n", cmd.Select(c => $"    <string>{SecurityElement.Escape(c)}</string>"))}
                  </array>
                  <key>RunAtLoad</key><true/>
                  <key>KeepAlive</key><true/>
                  <key>StandardOutPath</key><string>{logDir}/statement-downloader.log</string>
                  <key>StandardErrorPath</key><string>{logDir}/statement-downloader.log</string>
                </dict>
                </plist>
                """);
            Run("launchctl", "bootout", $"gui/{GetUid()}", plist);
            return Run("launchctl", "bootstrap", $"gui/{GetUid()}", plist);
        }
        Console.Error.WriteLine("Automatic start isn't set up on this OS. Start `statement-downloader serve` from your session's autostart.");
        return 1;
    }

    public static int Unregister()
    {
        if (OperatingSystem.IsWindows()) return Run("schtasks", "/Delete", "/TN", TaskName, "/F");
        if (OperatingSystem.IsMacOS())
        {
            var code = Run("launchctl", "bootout", $"gui/{GetUid()}", PlistPath());
            File.Delete(PlistPath());
            return code;
        }
        return 1;
    }

    private static string PlistPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", LaunchLabel + ".plist");

    private static string GetUid()
    {
        var psi = new ProcessStartInfo("id", "-u") { RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        var uid = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return uid;
    }

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

    private static int Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }
}
