using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace StatementDownloader.Secrets;

/// <summary>Stores small secrets (the Bitwarden access token, optional API tokens) in the OS credential store.</summary>
public interface ITokenStore
{
    string? Get(string name);
    void Set(string name, string value);
    bool Delete(string name);
    string Description { get; }
}

public static class TokenStores
{
    public const string ServiceName = "statement-downloader";

    public static ITokenStore ForCurrentOs() =>
        OperatingSystem.IsWindows() ? new WindowsCredentialStore()
        : OperatingSystem.IsMacOS() ? new MacKeychainStore()
        : new EnvironmentTokenStore();
}

/// <summary>Windows Credential Manager (generic credentials, per user).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialStore : ITokenStore
{
    public string Description => "Windows Credential Manager";
    private static string Target(string name) => $"{TokenStores.ServiceName}/{name}";

    public string? Get(string name)
    {
        if (!CredRead(Target(name), CredType.Generic, 0, out var ptr))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == 1168) return null; // ERROR_NOT_FOUND
            throw new Win32Exception(err);
        }
        try
        {
            var cred = Marshal.PtrToStructure<Credential>(ptr);
            return cred.CredentialBlobSize == 0 ? "" : Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2);
        }
        finally { CredFree(ptr); }
    }

    public void Set(string name, string value)
    {
        var blob = Encoding.Unicode.GetBytes(value);
        var blobPtr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var cred = new Credential
            {
                Type = CredType.Generic,
                TargetName = Target(name),
                UserName = Environment.UserName,
                CredentialBlob = blobPtr,
                CredentialBlobSize = (uint)blob.Length,
                Persist = 2, // CRED_PERSIST_LOCAL_MACHINE: this user, this machine, not roamed
            };
            if (!CredWrite(ref cred, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.Copy(new byte[blob.Length], 0, blobPtr, blob.Length);
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    public bool Delete(string name) => CredDelete(Target(name), CredType.Generic, 0);

    private enum CredType : uint { Generic = 1 }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public CredType Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, CredType type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, CredType type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}

/// <summary>macOS login keychain via the <c>security</c> tool.</summary>
public sealed class MacKeychainStore : ITokenStore
{
    public string Description => "macOS Keychain";

    public string? Get(string name)
    {
        var (code, output, _) = Run("find-generic-password", "-s", TokenStores.ServiceName, "-a", name, "-w");
        return code == 0 ? output.TrimEnd('\n') : null;
    }

    public void Set(string name, string value)
    {
        // -U updates an existing item. The value is passed as an argument, so it is briefly
        // visible to other processes of the same user; acceptable for a one-time setup command.
        var (code, _, err) = Run("add-generic-password", "-U", "-s", TokenStores.ServiceName, "-a", name, "-w", value);
        if (code != 0) throw new InvalidOperationException($"security add-generic-password failed: {err}");
    }

    public bool Delete(string name) => Run("delete-generic-password", "-s", TokenStores.ServiceName, "-a", name).Code == 0;

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        var psi = new ProcessStartInfo("/usr/bin/security") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output, error);
    }
}

/// <summary>Fallback (Linux, CI): reads <c>STATEMENT_DOWNLOADER_&lt;NAME&gt;</c> environment variables.</summary>
public sealed class EnvironmentTokenStore : ITokenStore
{
    public string Description => "environment variables";
    public static string VariableFor(string name) => "STATEMENT_DOWNLOADER_" + name.ToUpperInvariant().Replace('-', '_');
    public string? Get(string name) => Environment.GetEnvironmentVariable(VariableFor(name));
    public void Set(string name, string value) => throw new NotSupportedException($"Set the {VariableFor(name)} environment variable instead.");
    public bool Delete(string name) => false;
}
