using OstrivStudio.Core;
using System.Diagnostics;
using System.Reflection;

internal static class GameWriteCoordinatorTests
{
    public static async Task Run(string directory)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = Task.Run(() => GameWriteCoordinator.Run(() => { entered.Set(); release.Wait(); return 1; }));
        if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new Exception("First game write did not enter the gate.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { GameWriteCoordinator.Run(() => 2, cancelled.Token); throw new Exception("Cancelled game write entered the gate."); }
        catch (OperationCanceledException) { }
        bool secondEntered = false;
        var second = Task.Run(() => GameWriteCoordinator.Run(() => { secondEntered = true; return 2; }));
        if (second.Wait(TimeSpan.FromMilliseconds(100)) || secondEntered) throw new Exception("Game writes overlapped.");
        release.Set();
        if (await first != 1 || await second != 2 || !secondEntered) throw new Exception("The game write gate did not release.");
        Console.WriteLine("PASS game writes serialize and cancellation prevents a waiting edit");

        string signal = Path.Combine(directory, "game-write-child");
        using var parentEntered = new ManualResetEventSlim();
        using var parentRelease = new ManualResetEventSlim();
        var parent = Task.Run(() => GameWriteCoordinator.Run(() => { parentEntered.Set(); parentRelease.Wait(); return true; }));
        if (!parentEntered.Wait(TimeSpan.FromSeconds(5))) throw new Exception("Parent write did not enter the gate.");
        try
        {
            string executable = Environment.ProcessPath ?? throw new Exception("Test process path is unavailable.");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
            start.ArgumentList.Add("--game-write-probe"); start.ArgumentList.Add(signal);
            using var child = Process.Start(start) ?? throw new Exception("The child test process could not start.");
            var until = Stopwatch.StartNew();
            while (!File.Exists(signal + ".ready") && !child.HasExited && until.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(25);
            if (!File.Exists(signal + ".ready") || child.HasExited) throw new Exception("The child test process did not reach the write gate.");
            await Task.Delay(300);
            if (File.Exists(signal + ".entered")) throw new Exception("Writes in separate editor processes overlapped.");
            parentRelease.Set(); await parent;
            if (!child.WaitForExit(10000) || child.ExitCode != 0 || !File.Exists(signal + ".entered"))
                throw new Exception("The child write did not proceed after the first process released the gate.");
            Console.WriteLine("PASS compatible editor processes serialize game writes");
        }
        finally { parentRelease.Set(); await parent; }
    }
}
