using System.Text;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PlcSim.Slmp.Core.Diagnostics;

/// <summary>診断専用。通信フレームは記録しない。現在分と3世代を保持する。</summary>
public sealed class DiagnosticLog(string directory, int maxBytes = 1024 * 1024)
{
    private readonly object _sync = new();
    public static DiagnosticLog Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlcSim.Slmp", "logs"));
    public string DirectoryPath { get; } = directory;

    public string DirectoryPathForShell()
    {
        Directory.CreateDirectory(DirectoryPath);
        if (!OperatingSystem.IsWindows()) return DirectoryPath;
        // An inherited MSIX identity can redirect LocalAppData. Explorer does not
        // inherit that redirection, so resolve the file's physical location first.
        using var file = File.OpenHandle(Path.Combine(DirectoryPath, "diagnostic.log"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        var path = new StringBuilder(512);
        var length = GetFinalPathNameByHandle(file, path, (uint)path.Capacity, 0);
        if (length >= path.Capacity)
        {
            path.Capacity = checked((int)length + 1);
            length = GetFinalPathNameByHandle(file, path, (uint)path.Capacity, 0);
        }
        if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var resolved = path.ToString();
        if (resolved.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) resolved = @"\\" + resolved[8..];
        else if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved[4..];
        return Path.GetDirectoryName(resolved)!;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);

    public void Write(string message, Exception? exception = null)
    {
        try
        {
            lock (_sync)
            {
                Directory.CreateDirectory(DirectoryPath);
                var path = Path.Combine(DirectoryPath, "diagnostic.log");
                var line = $"{DateTimeOffset.Now:O} [{Environment.ProcessId}] {message} {exception}\n";
                // Bound a single exception as well as the file, including UTF-8 expansion.
                if (line.Length > maxBytes / 4) line = line[..(maxBytes / 4)] + "…\n";
                var bytes = Encoding.UTF8.GetBytes(line);
                if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > maxBytes)
                {
                    for (var i = 2; i >= 0; i--)
                    {
                        var source = i == 0 ? path : path + "." + i;
                        if (File.Exists(source)) File.Move(source, path + "." + (i + 1), overwrite: true);
                    }
                }
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                stream.Write(bytes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            System.Diagnostics.Trace.WriteLine($"Diagnostic log unavailable: {ex.Message}");
        }
    }
}
