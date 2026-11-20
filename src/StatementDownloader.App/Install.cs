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
            // schtasks /SC ONLOGON needs admin; a task scoped to the current user doesn't.
            // --hidden makes the agent hide its console window and log to a file.
            static string Ps(string s) => "'" + s.Replace("'", "''") + "'";
            var exe = cmd[0];
            var exeArgs = string.Join(' ', cmd.Skip(1).Append("--hidden").Select(Quote));
            var script = $"""
                $u = "$env:USERDOMAIN\$env:USERNAME"
                $a = New-ScheduledTaskAction -Execute {Ps(exe)} -Argument {Ps(exeArgs)} -WorkingDirectory {Ps(Path.GetDirectoryName(exe)!)}
                $t = New-ScheduledTaskTrigger -AtLogOn -User $u
                $s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 5) -StartWhenAvailable
                $p = New-ScheduledTaskPrincipal -UserId $u -LogonType Interactive -RunLevel Limited
                Register-ScheduledTask -TaskName {Ps(TaskName)} -Action $a -Trigger $t -Settings $s -Principal $p -Force | Out-Null
                Start-ScheduledTask -TaskName {Ps(TaskName)}
                """;
            var code = Run("powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script);
            if (code == 0) Console.WriteLine($"Registered '{TaskName}' to start at sign-in, and started it.");
            return code;
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
        if (OperatingSystem.IsWindows())
            return Run("powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
                $"Stop-ScheduledTask -TaskName '{TaskName}' -ErrorAction SilentlyContinue; Unregister-ScheduledTask -TaskName '{TaskName}' -Confirm:$false");
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

    /// <summary>Windows: hide this process's console window and send output to a log file.</summary>
    public static void RunHidden(string logFile)
    {
        if (!OperatingSystem.IsWindows()) return;
        var hwnd = GetConsoleWindow();
        if (hwnd != IntPtr.Zero) ShowWindow(hwnd, 0); // SW_HIDE
        Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
        if (File.Exists(logFile) && new FileInfo(logFile).Length > 5_000_000) File.Move(logFile, logFile + ".old", overwrite: true);
        var writer = new StreamWriter(new FileStream(logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        Console.SetOut(writer);
        Console.SetError(writer);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
