namespace MaddoxTasks.Worker;

using System.Text;

public sealed record ProgramOptions(bool Stop, string? ConfigPath)
{
    public static ProgramOptions Parse(string[] args) => args switch
    {
        [] => new(false, null),
        ["--stop"] => new(true, null),
        [var configPath] when !configPath.StartsWith("-", StringComparison.Ordinal) => new(false, configPath),
        _ => throw new ArgumentException("Usage: MaddoxTasks.Worker [config-path] | --stop")
    };
}

public static class Program
{
    internal const string StopEventName = "Local\\MaddoxTasks.Worker.Stop";

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = ProgramOptions.Parse(args);
            if (options.Stop) return SignalStop();

            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

            using var mutex = new Mutex(true, "Local\\MaddoxTasks.Worker", out var firstInstance);
            if (!firstInstance) { Console.Error.WriteLine("Worker is already running."); return 2; }
            using var cancellation = new CancellationTokenSource();
            using var stopEvent = new EventWaitHandle(false, EventResetMode.AutoReset, StopEventName);
            var stopRegistration = ThreadPool.RegisterWaitForSingleObject(stopEvent, (_, _) => cancellation.Cancel(), null, Timeout.Infinite, true);
            Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
            var configPath = options.ConfigPath ?? Path.Combine(AppContext.BaseDirectory, "worker.json");
            try { await new WorkerHost(configPath).RunAsync(cancellation.Token); }
            finally { stopRegistration.Unregister(null); }
            return 0;
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception exception) { Console.Error.WriteLine(exception.Message); return 1; }
    }

    private static int SignalStop()
    {
        try
        {
            using var stopEvent = EventWaitHandle.OpenExisting(StopEventName);
            stopEvent.Set();
            return 0;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            Console.Error.WriteLine("Worker is not running.");
            return 1;
        }
    }
}
