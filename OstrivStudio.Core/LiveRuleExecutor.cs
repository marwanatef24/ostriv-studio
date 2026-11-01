using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;

namespace OstrivStudio.Core;

public sealed record LiveRuleResult(DiscoveryResult Snapshot, int Applied, ExistingRuleExecutor.Plan? Plan = null);

// This pause flag and timing layout are specific to SupportedHash. The game
// can still autosave while paused; this is not an atomic game transaction.
public sealed class GamePauseControl
{
    private const ulong WorldPointerRva = 0x680a80, PauseOffset = 0x11bf98;
    private const ulong CurrentSpeedRva = 0x653160, RequestedSpeedRva = 0x653868, DeltaRva = 0x67a690, GameTimeRva = 0x653170;
    private readonly FishBulkEditor.IMemory _memory;
    private readonly ulong _base;
    private ulong _world;
    private double _speed;
    private bool _changed, _attempted;

    public GamePauseControl(FishBulkEditor.IMemory memory, ulong moduleBase) { _memory = memory; _base = moduleBase; }
    private ulong Pointer(ulong address) => BinaryPrimitives.ReadUInt64LittleEndian(_memory.Read(address, 8));
    private double Double(ulong address) => BitConverter.ToDouble(_memory.Read(address, 8));
    private byte Flag() => _memory.Read(checked(_world + PauseOffset), 1)[0];
    private void RequireIdentity()
    {
        if (Pointer(_base + WorldPointerRva) != _world || Double(_base + RequestedSpeedRva) != _speed)
            throw new InvalidOperationException("The game world or speed changed during the edit.");
    }
    public void Acquire(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        _world = Pointer(_base + WorldPointerRva);
        if (_world < 0x10000 || _world > (ulong)long.MaxValue - PauseOffset) throw new InvalidOperationException("The game world is unavailable.");
        _speed = Double(_base + RequestedSpeedRva);
        if (_speed is not (1 or 5 or 10 or 12)) throw new InvalidOperationException("The game speed is not recognized.");
        byte initial = Flag();
        if (initial > 1) throw new InvalidOperationException("The game's pause state is not recognized.");
        try
        {
            if (initial == 0)
            {
                RequireIdentity();
                if (Flag() != 0) throw new InvalidOperationException("The game pause state changed before editing.");
                _attempted = true;
                _memory.Write(checked(_world + PauseOffset), [1]);
                _changed = true;
            }
            var clock = Stopwatch.StartNew();
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                RequireIdentity();
                if (Flag() != 1) throw new InvalidOperationException("The game was resumed before the edit.");
                if (Double(_base + CurrentSpeedRva) == 0 && Double(_base + DeltaRva) == 0) break;
                if (clock.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("The game did not stop advancing.");
                Thread.Sleep(40);
            }
            byte[] gameTime = _memory.Read(_base + GameTimeRva, 8);
            Thread.Sleep(200);
            EnsurePaused();
            if (!gameTime.AsSpan().SequenceEqual(_memory.Read(_base + GameTimeRva, 8)))
                throw new InvalidOperationException("Game time advanced while waiting for pause.");
        }
        catch
        {
            if (_attempted) TryResumeAfterFailure();
            throw;
        }
    }
    public void EnsurePaused()
    {
        RequireIdentity();
        if (Flag() != 1 || Double(_base + CurrentSpeedRva) != 0 || Double(_base + DeltaRva) != 0)
            throw new InvalidOperationException("The game is no longer paused; the edit cannot continue.");
    }
    private void TryResumeAfterFailure()
    {
        try { if (Pointer(_base + WorldPointerRva) == _world && Flag() == 1 && Double(_base + RequestedSpeedRva) == _speed) _memory.Write(checked(_world + PauseOffset), [0]); }
        catch { /* Preserve the original failure; the UI asks the player to inspect the game. */ }
        _changed = false;
    }
    public void Resume()
    {
        if (!_changed) return;
        EnsurePaused();
        _memory.Write(checked(_world + PauseOffset), [0]);
        _changed = false;
        if (Flag() != 0) throw new InvalidOperationException("The game pause state could not be restored; inspect the game.");
    }
    public void ResumeAfterPreflightFailure() { if (_changed) TryResumeAfterFailure(); }
}

public static class LiveRuleExecutor
{
    private sealed class GuardedMemory(FishBulkEditor.IMemory inner, GamePauseControl pause) : FishBulkEditor.IMemory
    {
        public byte[] Read(ulong address, int size) => inner.Read(address, size);
        public void Write(ulong address, byte[] data) { pause.EnsurePaused(); inner.Write(address, data); }
    }

    // Re-read each rule-matching object after the simulation has stopped.
    // Object addresses are session-only candidates, never persistent IDs.
    public static DiscoveryResult Refresh(DiscoveryResult scan, AutomationProfileSettings settings, FishBulkEditor.IMemory memory)
    {
        var types = settings.MaintenanceRules.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.BuildingType))
            .Select(r => r.BuildingType.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var buildings = new List<BuildingCandidate>(scan.Buildings.Count);
        foreach (var b in scan.Buildings)
        {
            if (!types.Contains(b.ClassName)) { buildings.Add(b); continue; }
            int length = scan.PointerOffset + 8;
            byte[] header = memory.Read(b.Address, length);
            int count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(scan.CountOffset));
            ulong pointer = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(scan.PointerOffset));
            if (BinaryPrimitives.ReadUInt64LittleEndian(header) != b.Vtable || pointer != b.ResourcePointer || count is < 0 or > 511)
                throw new InvalidOperationException("A reviewed building changed while the game was running; scan again.");
            byte[] data = count == 0 ? [] : memory.Read(pointer, checked(count * 20));
            var resources = BuildingDiscovery.DecodeResources(data, count) ?? throw new InvalidOperationException("A refreshed resource array is invalid.");
            if (!header.AsSpan().SequenceEqual(memory.Read(b.Address, length)) ||
                (count != 0 && !data.AsSpan().SequenceEqual(memory.Read(pointer, data.Length))))
                throw new InvalidOperationException("A building changed while it was being refreshed.");
            buildings.Add(b with { ResourceCount = count, Resources = resources });
        }
        return scan with { Buildings = buildings };
    }

    public static LiveRuleResult ApplyToGame(ProfileDocument profile, DiscoveryResult scan, CancellationToken cancellation, Action<DiscoveryResult>? validateFresh = null)
    {
        cancellation.ThrowIfCancellationRequested();
        if (scan.GameSha256 != FishBulkEditor.SupportedHash) throw new InvalidOperationException("The game build is not supported.");
        using (var file = File.Open(scan.GamePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            if (Convert.ToHexString(SHA256.HashData(file)) != FishBulkEditor.SupportedHash) throw new InvalidOperationException("The executable changed.");
        using var process = Process.GetProcessById(scan.ProcessId);
        if (process.HasExited || process.StartTime.ToUniversalTime() != scan.ProcessStartedUtc ||
            !string.Equals(Path.GetFullPath(process.MainModule!.FileName), Path.GetFullPath(scan.GamePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The game session changed; scan again.");
        ulong moduleBase = checked((ulong)process.MainModule.BaseAddress.ToInt64());
        using var memory = new ExistingRuleExecutor.ProcessMemory(scan.ProcessId);
        var image = BuildingDiscovery.LoadImage(scan.GamePath).Image;
        const int pauseFunction = 0x3f5ec0, checkedLength = 32;
        if (!image.AsSpan(pauseFunction, checkedLength).SequenceEqual(memory.Read(moduleBase + pauseFunction, checkedLength)))
            throw new InvalidOperationException("The running game's pause control differs from the tested build.");
        var control = new GamePauseControl(memory, moduleBase);
        control.Acquire(cancellation);
        bool appliedResources = false;
        try
        {
            var settings = profile.AutomationProfiles[profile.SelectedProfile];
            var fresh = Refresh(scan, settings, memory);
            cancellation.ThrowIfCancellationRequested();
            control.EnsurePaused();
            validateFresh?.Invoke(fresh);
            var result = ExistingRuleExecutor.ApplyCheckedDetailed(profile, fresh, new GuardedMemory(memory, control));
            appliedResources = result.Applied > 0;
            control.Resume();
            return result;
        }
        catch (PartialGameWriteException)
        {
            // Do not resume a simulation that may have partially changed.
            throw;
        }
        catch (Exception ex) when (appliedResources)
        {
            throw new PartialGameWriteException("Resource edits completed but the game state could not be restored. Inspect the game and reload the backup if uncertain. " + ex.Message, ex);
        }
        catch
        {
            control.ResumeAfterPreflightFailure();
            throw;
        }
    }
}
