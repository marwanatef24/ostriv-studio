namespace OstrivStudio.Core;

public static class BackupRetry
{
    public static async Task<T> RunAsync<T>(Func<Task<T>> backup, Action<int> retrying,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken cancellation)
    {
        for (int attempt = 1; ; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            try { return await backup().ConfigureAwait(false); }
            catch (IOException) when (attempt < 3)
            {
                cancellation.ThrowIfCancellationRequested();
                retrying(attempt);
                await delay(TimeSpan.FromSeconds(1), cancellation).ConfigureAwait(false);
            }
        }
    }
}
