namespace OstrivStudio.Core;

// Serializes editor-owned game writes, including the pause/restore sequence.
// The named mutex also coordinates compatible Ostriv Studio processes. Other
// tools and older versions cannot be assumed to participate.
public static class GameWriteCoordinator
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _uncertain;

    public static T Run<T>(Func<T> action, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        Gate.Wait(cancellation);
        Mutex? crossProcess = null;
        bool ownsMutex = false;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (_uncertain) throw new InvalidOperationException("A previous game edit may be incomplete. Restart the editor after checking the game.");
            crossProcess = new Mutex(false, @"Local\OstrivStudio.GameWrite.v1");
            try
            {
                while (!crossProcess.WaitOne(TimeSpan.FromMilliseconds(200))) cancellation.ThrowIfCancellationRequested();
                ownsMutex = true;
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true; _uncertain = true;
                throw new InvalidOperationException("Another editor stopped during a game edit. Check the game and restart the editor before writing again.");
            }
            cancellation.ThrowIfCancellationRequested();
            try { return action(); }
            catch (PartialGameWriteException) { _uncertain = true; throw; }
        }
        finally
        {
            try { if (ownsMutex) crossProcess!.ReleaseMutex(); }
            finally { crossProcess?.Dispose(); Gate.Release(); }
        }
    }
}
