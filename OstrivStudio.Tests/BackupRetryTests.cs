using OstrivStudio.Core;

internal static class BackupRetryTests
{
    public static async Task Run()
    {
        int attempts = 0, retries = 0;
        var result = await BackupRetry.RunAsync(() => ++attempts < 3 ? Task.FromException<int>(new IOException("autosave rotated")) : Task.FromResult(42),
            _ => retries++, (_, _) => Task.CompletedTask, CancellationToken.None);
        if (result != 42 || attempts != 3 || retries != 2) throw new Exception("Backup retry did not recover after transient failures.");
        attempts = 0;
        try
        {
            await BackupRetry.RunAsync(() => { attempts++; return Task.FromException<int>(new IOException("still busy")); },
                _ => { }, (_, _) => Task.CompletedTask, CancellationToken.None);
            throw new Exception("Persistent backup failure was ignored.");
        }
        catch (IOException) when (attempts == 3) { }
        using var stopped = new CancellationTokenSource(); stopped.Cancel();
        try
        {
            await BackupRetry.RunAsync(() => Task.FromResult(0), _ => { }, (_, _) => Task.CompletedTask, stopped.Token);
            throw new Exception("Stopped backup was started.");
        }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS backup retries transient autosaves, stops after three failures, and honors cancellation");
    }
}
