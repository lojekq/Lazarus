using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using Wdk = Windows.Wdk;

namespace Lazarus.Features;

/// <summary>What a shell's PEB says: current directory and the variables Lazarus needs.</summary>
public sealed record ShellState(string? Cwd, string? LazarusCwd, string? WtSession, string? WtProfile);

/// <summary>
/// Reads the current directory and environment of a 64-bit shell from its PEB (undocumented, offsets in <see cref="NativeFacts"/>).
/// Only for shells and script hosts (spec 03 §5): it needs PROCESS_VM_READ. Any failure → null, never an exception.
/// </summary>
public static unsafe class Peb
{
    public static ShellState? Read(int pid)
    {
        if (!Environment.Is64BitProcess) return null;
        try
        {
            using var h = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_ACCESS_RIGHTS.PROCESS_VM_READ, false, (uint)pid);
            if (h.IsInvalid) return null;
            var handle = (HANDLE)h.DangerousGetHandle();
            if (!PInvoke.IsWow64Process(h, out var wow) || wow) return null; // a 32-bit process keeps its own PEB

            var pbi = stackalloc nint[6]; // PROCESS_BASIC_INFORMATION: ExitStatus, PebBaseAddress, ...
            uint len = 0;
            if (Wdk.PInvoke.NtQueryInformationProcess(handle, Wdk.System.Threading.PROCESSINFOCLASS.ProcessBasicInformation, pbi, (uint)(6 * sizeof(nint)), ref len).Value < 0) return null;
            var peb = pbi[1];
            var p = ReadPtr(handle, peb + NativeFacts.PebProcessParameters);
            if (p == 0) return null;

            var cwd = ReadUnicodeString(handle, p + NativeFacts.ParamsCurrentDirectory);
            var envAt = ReadPtr(handle, p + NativeFacts.ParamsEnvironment);
            var envSize = (long)ReadPtr(handle, p + NativeFacts.ParamsEnvironmentSize);
            var env = envAt == 0 ? [] : ParseEnvironment(ReadChars(handle, envAt, (int)Math.Clamp(envSize, 0, NativeFacts.MaxEnvironmentBytes) / 2));
            return new(TrimSlash(cwd), TrimSlash(env.GetValueOrDefault("LAZARUS_CWD")), env.GetValueOrDefault("WT_SESSION"), env.GetValueOrDefault("WT_PROFILE_ID"));
        }
        catch { return null; }
    }

    /// <summary>"A=1\0B=2\0\0"; names compare case-insensitively, as Windows does. Lines starting with '=' (per-drive cwd) are skipped.</summary>
    internal static Dictionary<string, string> ParseEnvironment(string block)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in block.Split('\0'))
        {
            if (line.Length == 0) break; // the double \0 ends the block; the rest of the buffer is garbage
            var eq = line.IndexOf('=');
            if (eq > 0) d.TryAdd(line[..eq], line[(eq + 1)..]);
        }
        return d;
    }

    /// <summary>The PEB keeps "C:\dir\" with a trailing slash; a root stays "C:\".</summary>
    static string? TrimSlash(string? path) =>
        string.IsNullOrEmpty(path) ? null : path.Length > 3 && path.EndsWith('\\') ? path[..^1] : path;

    static nint ReadPtr(HANDLE h, nint at)
    {
        nint v = 0;
        return PInvoke.ReadProcessMemory(h, (void*)at, &v, (nuint)sizeof(nint), null) ? v : 0;
    }

    static string? ReadUnicodeString(HANDLE h, nint at)
    {
        var us = stackalloc byte[16]; // UNICODE_STRING x64: ushort Length, ushort MaximumLength, 4 padding, PWSTR Buffer
        if (!PInvoke.ReadProcessMemory(h, (void*)at, us, 16, null)) return null;
        var length = *(ushort*)us;
        var buffer = *(nint*)(us + 8);
        return length == 0 || buffer == 0 ? null : ReadChars(h, buffer, length / 2);
    }

    static string ReadChars(HANDLE h, nint at, int chars)
    {
        if (chars <= 0) return "";
        var buf = new char[chars];
        fixed (char* b = buf)
        {
            nuint read = 0;
            // ponytail: a block that changes while we read is just read again on the next tick
            if (!PInvoke.ReadProcessMemory(h, (void*)at, b, (nuint)(chars * 2), &read) && read == 0) return "";
            return new string(b, 0, (int)read / 2);
        }
    }
}
