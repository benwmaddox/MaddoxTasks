using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace MaddoxTasks.Worker;

public sealed record ExecResult(int ExitCode, string Output, string Error);
public sealed record TerminalOutputDirective(Func<string, bool> IsTerminal, TimeSpan GracePeriod);

public interface IProcessRunner
{
    Task<ExecResult> RunAsync(string executable, IEnumerable<string> arguments, string workingDirectory, CancellationToken cancellationToken, Action<string>? outputLine = null, TerminalOutputDirective? terminalOutput = null, string? standardInput = null);
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

    public async Task<ExecResult> RunAsync(string executable, IEnumerable<string> arguments, string workingDirectory, CancellationToken cancellationToken, Action<string>? outputLine = null, TerminalOutputDirective? terminalOutput = null, string? standardInput = null)
    {
        var argumentList = ProcessArguments.Prepare(executable, arguments, workingDirectory);
        log.Write("info", "process.start", new { executable = Path.GetFileName(executable), argumentCount = argumentList.Length, standardInputLength = standardInput?.Length ?? 0, workingDirectory });
        using var process = new Process { StartInfo = CreateStartInfo(executable, workingDirectory) };
        process.StartInfo.RedirectStandardInput = standardInput is not null;
        if (standardInput is not null) process.StartInfo.StandardInputEncoding = new UTF8Encoding(false);
        foreach (var argument in argumentList) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        containment.Add(process);
        using var registration = cancellationToken.Register(() => { try { process.Kill(true); } catch { } });
        var output = new StringBuilder();
        var error = new StringBuilder();
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalObserved = false;
        var outputTask = ReadAsync(process.StandardOutput, output, line =>
        {
            outputLine?.Invoke(line);
            if (terminalOutput is not null && terminalOutput.IsTerminal(line))
            {
                terminalObserved = true;
                terminal.TrySetResult();
            }
        });
        var errorTask = ReadAsync(process.StandardError, error, null);
        var inputTask = WriteInputAsync(process, standardInput, cancellationToken);
        var exitTask = process.WaitForExitAsync(cancellationToken);
        if (terminalOutput is null) await exitTask;
        else if (await Task.WhenAny(exitTask, terminal.Task) == terminal.Task)
        {
            var graceful = Task.Delay(terminalOutput.GracePeriod, cancellationToken);
            if (await Task.WhenAny(exitTask, graceful) != exitTask)
            {
                cancellationToken.ThrowIfCancellationRequested();
                log.Write("warning", "process.terminal.force-kill", new { executable = Path.GetFileName(executable), gracePeriod = terminalOutput.GracePeriod });
                try { process.Kill(true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(cancellationToken);
            }
        }
        else await exitTask;
        await Task.WhenAll(outputTask, errorTask);
        try { await inputTask; }
        catch (IOException) when (process.ExitCode != 0 || terminalObserved) { /* Preserve the child failure when it closes stdin early. */ }
        var result = new ExecResult(terminalObserved ? 0 : process.ExitCode, output.ToString(), error.ToString());
        log.Write(result.ExitCode == 0 ? "info" : "error", "process.exit", new { executable = Path.GetFileName(executable), exitCode = result.ExitCode, outputLength = result.Output.Length, error = SafeError(result.Error) });
        return result;
    }

    private static async Task WriteInputAsync(Process process, string? input, CancellationToken cancellationToken)
    {
        if (input is null) return;
        using var writer = process.StandardInput;
        await writer.WriteAsync(input.AsMemory(), cancellationToken);
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
    // Worker Codex builders always put the prompt last, for both exec and exec resume.
    public static (string[] Arguments, string StandardInput) WithPromptOnStandardInput(IEnumerable<string> arguments)
    {
        var values = arguments.ToArray();
        var prompt = values[^1];
        values[^1] = "-";
        return (values, prompt);
    }

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
