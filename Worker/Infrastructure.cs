using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace MaddoxTasks.Worker;

public sealed record ExecResult(int ExitCode, string Output, string Error);

public interface IProcessRunner
{
    Task<ExecResult> RunAsync(string executable, IEnumerable<string> arguments, string workingDirectory, CancellationToken cancellationToken, Action<string>? outputLine = null);
}

public interface IChildProcessContainment : IDisposable { void Add(Process process); }
public sealed class NoopContainment : IChildProcessContainment { public void Add(Process process) { } public void Dispose() { } }

public static class ChildProcessContainmentFactory
{
    public static IChildProcessContainment Create(bool isWindows) => isWindows ? new WindowsJobContainment() : new NoopContainment();
}

public sealed class ProcessRunner : IProcessRunner, IDisposable
{
    private readonly IChildProcessContainment containment;
    private readonly IRollingLog log;
    public ProcessRunner(IChildProcessContainment containment, IRollingLog log) { this.containment = containment; this.log = log; }

    public async Task<ExecResult> RunAsync(string executable, IEnumerable<string> arguments, string workingDirectory, CancellationToken cancellationToken, Action<string>? outputLine = null)
    {
        var argumentList = ProcessArguments.Prepare(executable, arguments, workingDirectory);
        log.Write("info", "process.start", new { executable = Path.GetFileName(executable), argumentCount = argumentList.Length, workingDirectory });
        using var process = new Process { StartInfo = CreateStartInfo(executable, workingDirectory) };
        foreach (var argument in argumentList) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        containment.Add(process);
        using var registration = cancellationToken.Register(() => { try { process.Kill(true); } catch { } });
        var output = new StringBuilder();
        var error = new StringBuilder();
        var outputTask = ReadAsync(process.StandardOutput, output, outputLine);
        var errorTask = ReadAsync(process.StandardError, error, null);
        await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(cancellationToken));
        var result = new ExecResult(process.ExitCode, output.ToString(), error.ToString());
        log.Write(result.ExitCode == 0 ? "info" : "error", "process.exit", new { executable = Path.GetFileName(executable), exitCode = result.ExitCode, outputLength = result.Output.Length, error = SafeError(result.Error) });
        return result;
    }

    public void Dispose() => containment.Dispose();
    public static ProcessStartInfo CreateStartInfo(string executable, string workingDirectory) => new()
    {
        FileName = executable,
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
        CreateNoWindow = true
    };
    private static async Task ReadAsync(StreamReader reader, StringBuilder target, Action<string>? callback) { while (await reader.ReadLineAsync() is { } line) { target.AppendLine(line); callback?.Invoke(line); } }
    private static string SafeError(string error) => error.Length > 1000 ? error[..1000] : error;
}

public static class ProcessArguments
{
    public static string[] Prepare(string executable, IEnumerable<string> arguments, string workingDirectory)
    {
        var values = arguments.ToArray();
        if (!Path.GetFileNameWithoutExtension(executable).Equals("git", StringComparison.OrdinalIgnoreCase)) return values;

        var safeDirectory = Path.GetFullPath(workingDirectory).Replace('\\', '/');
        return ["-c", $"safe.directory={safeDirectory}", .. values];
    }
}

public interface IRollingLog { void Write(string level, string message, object? data = null); }
public sealed class RollingLog : IRollingLog
{
    private static readonly System.Text.RegularExpressions.Regex Secret = new("(?i)(github_pat_[A-Za-z0-9_]+|gh[pousr]_[A-Za-z0-9]+|sk-[A-Za-z0-9_-]{16,}|(?:token|password|secret)[=: ]+[^\\s\\\"]+)", System.Text.RegularExpressions.RegexOptions.Compiled);
    private readonly string directory;
    private readonly IClock clock;
    private readonly object gate = new();
    public RollingLog(string directory, IClock clock) { this.directory = directory; this.clock = clock; Directory.CreateDirectory(directory); }
    public void Write(string level, string message, object? data = null)
    {
        var line = Secret.Replace(JsonSerializer.Serialize(new { timestamp = clock.UtcNow, level, message, data }), "[redacted]");
        lock (gate) File.AppendAllText(Path.Combine(directory, $"worker-{clock.UtcNow:yyyyMMdd}.jsonl"), line + Environment.NewLine);
    }
}

public sealed class WindowsJobContainment : IChildProcessContainment
{
    private const uint KillOnJobClose = 0x2000;
    private readonly SafeFileHandle handle;
    public WindowsJobContainment()
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception();
        var information = new ExtendedLimitInformation { BasicLimitInformation = new BasicLimitInformation { LimitFlags = KillOnJobClose } };
        var length = Marshal.SizeOf(information);
        var pointer = Marshal.AllocHGlobal(length);
        try { Marshal.StructureToPtr(information, pointer, false); if (!SetInformationJobObject(handle, 9, pointer, (uint)length)) throw new System.ComponentModel.Win32Exception(); }
        finally { Marshal.FreeHGlobal(pointer); }
    }
    public void Add(Process process) { if (!AssignProcessToJobObject(handle, process.Handle)) throw new System.ComponentModel.Win32Exception(); }
    public void Dispose() => handle.Dispose();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, IntPtr information, uint length);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimitInformation { public long PerProcessUserTimeLimit; public long PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize; public UIntPtr MaximumWorkingSetSize; public uint ActiveProcessLimit; public long Affinity; public uint PriorityClass; public uint SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount; public ulong WriteOperationCount; public ulong OtherOperationCount; public ulong ReadTransferCount; public ulong WriteTransferCount; public ulong OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimitInformation { public BasicLimitInformation BasicLimitInformation; public IoCounters IoInfo; public UIntPtr ProcessMemoryLimit; public UIntPtr JobMemoryLimit; public UIntPtr PeakProcessMemoryUsed; public UIntPtr PeakJobMemoryUsed; }
}
