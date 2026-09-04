namespace MaddoxTasks.Worker;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var mutex = new Mutex(true, "Local\\MaddoxTasks.Worker", out var firstInstance);
        if (!firstInstance) { Console.Error.WriteLine("Worker is already running."); return 2; }
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        try
        {
            var configPath = args.FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "worker.json");
            await new WorkerHost(configPath).RunAsync(cancellation.Token);
            return 0;
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception exception) { Console.Error.WriteLine(exception.Message); return 1; }
    }
}
