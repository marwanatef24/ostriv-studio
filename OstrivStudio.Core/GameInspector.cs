using System.ComponentModel;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace OstrivStudio.Core;

public sealed record GameReport(string Path, string Sha256, int? ProcessId, bool LiveRead, string Detail, IReadOnlyList<PatternResult> Patterns)
{
    public int UniqueCount => Patterns.Count(x => x.Matches == 1);
    public bool CanWrite => false; // A pattern match alone never validates a write.
}

public static class GameInspector
{
    private const int MaxExecutableBytes = 128 * 1024 * 1024;
    private const int MaxCodeBytes = 64 * 1024 * 1024;
    private const uint ProcessQueryInformation = 0x0400, ProcessVmRead = 0x0010;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(SafeProcessHandle process, IntPtr address, byte[] buffer, nuint size, out nuint read);

    public static GameReport Inspect(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        if (!string.Equals(System.IO.Path.GetFileName(path), "ostriv.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select the game's ostriv.exe file.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length <= 0 || stream.Length > MaxExecutableBytes) throw new InvalidDataException("Game executable size is outside the supported inspection limit.");
        string hash = Convert.ToHexString(SHA256.HashData(stream));
        stream.Position = 0;
        using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
        if (pe.PEHeaders.PEHeader?.Magic != PEMagic.PE32Plus || pe.PEHeaders.CoffHeader.Machine != Machine.Amd64)
            throw new InvalidDataException("Expected a 64-bit Windows game executable.");
        var sections = pe.PEHeaders.SectionHeaders.Where(s => s.Name == ".text").ToArray();
        if (sections.Length != 1) throw new InvalidDataException("Game executable must contain one .text section.");
        var section = sections[0];
        if (section.SizeOfRawData <= 0 || section.SizeOfRawData > MaxCodeBytes || section.VirtualSize <= 0 || section.VirtualSize > MaxCodeBytes)
            throw new InvalidDataException("Game code section size is outside the supported inspection limit.");
        byte[] code = pe.GetSectionData(section.VirtualAddress).GetContent(0, Math.Min(section.SizeOfRawData, section.VirtualSize)).ToArray();
        int? processId = null;
        bool live = false;
        string detail = "Game is not running; scanned the executable on disk.";
        var candidates = Process.GetProcessesByName("ostriv");
        try
        {
            foreach (var process in candidates)
            {
                try
                {
                    var module = process.MainModule;
                    if (module is null || !string.Equals(System.IO.Path.GetFullPath(module.FileName), path, StringComparison.OrdinalIgnoreCase)) continue;
                    processId = process.Id;
                    if (section.VirtualAddress < 0 || (long)section.VirtualAddress + section.VirtualSize > module.ModuleMemorySize)
                        throw new InvalidDataException("Code section is outside the running game module.");
                    using var handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, process.Id);
                    if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                    var buffer = new byte[section.VirtualSize];
                    var address = new IntPtr(checked(module.BaseAddress.ToInt64() + section.VirtualAddress));
                    if (!ReadProcessMemory(handle, address, buffer, (nuint)buffer.Length, out var read) || read != (nuint)buffer.Length)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to read the entire game code section.");
                    if (process.HasExited) throw new InvalidOperationException("The game closed during inspection.");
                    code = buffer;
                    live = true;
                    detail = "Scanned the running game's main code section using read-only access.";
                    break;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or System.IO.IOException or InvalidDataException)
                { detail = "Live inspection unavailable: " + ex.Message + " Scanned the executable on disk."; }
            }
        }
        finally { foreach (var process in candidates) process.Dispose(); }
        var results = Patterns.Known.Select(p => new PatternResult(p.Name, Patterns.CountMatches(code, p.Pattern))).ToArray();
        return new(path, hash, processId, live, detail, results);
    }
}
