using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace OstrivStudio.Core;

public sealed record LegacyImportResult(string SourceSha256, IReadOnlyList<MaintenanceRule> Rules, IReadOnlyList<CapacityRule> Capacities);

// The original database is never opened by SQLite. A bounded, twice-read snapshot is
// deserialized read-only in memory; an active journal or WAL is not safe to import.
public static class LegacyRuleImport
{
    private const int MaxBytes = 32 * 1024 * 1024, MaxRows = 10000;
    public static LegacyImportResult Read(string path)
    {
        string full = Path.GetFullPath(path);
        static bool JournalExists(string p) => File.Exists(p + "-wal") || File.Exists(p + "-journal");
        static byte[] ReadBytes(string p)
        {
            using var file = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length is < 100 or > MaxBytes) throw new InvalidDataException("The original preference file has an unexpected size.");
            byte[] bytes = new byte[checked((int)file.Length)];
            file.ReadExactly(bytes);
            if (file.Length != bytes.Length) throw new InvalidDataException("The preference file changed while reading.");
            return bytes;
        }
        if (JournalExists(full)) throw new InvalidDataException("The original editor has an active database journal. Close it and try again.");
        var bytes = ReadBytes(full);
        var second = ReadBytes(full);
        if (JournalExists(full) || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), SHA256.HashData(second)))
            throw new InvalidDataException("The preference database changed during import. Close the original editor and try again.");
        if (!bytes.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8) || bytes[18] != 1 || bytes[19] != 1 ||
            BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(60, 4)) != 6)
            throw new InvalidDataException("This is not a supported version 6 preference database in rollback mode.");

        var rules = new List<MaintenanceRule>();
        var capacities = new List<CapacityRule>();
        using var db = new SnapshotDb(bytes);
        if (db.ScalarText("PRAGMA quick_check(1)") != "ok") throw new InvalidDataException("The preference database failed its integrity check.");
        db.RequireOrdinaryTables();
        db.Rows("SELECT RES_ID, AMOUNT, BLD_TYPE, BLD_CNT, MAINTAINED, MAINTAIN_TYPE FROM RES_MAINTAINED_TABLE", 6, stmt =>
        {
            if (rules.Count >= MaxRows) throw new InvalidDataException("There are too many maintenance rules.");
            int resource = db.Integer(stmt, 0), building = db.Integer(stmt, 2), limit = db.Integer(stmt, 3);
            int enabled = db.Integer(stmt, 4), mode = db.Integer(stmt, 5);
            if (resource is <= 0 or > 65535 || building <= 0 || limit is < -1 or > 100000 || enabled is < 0 or > 1 || mode is < 0 or > 1)
                throw new InvalidDataException("An original maintenance rule contains an unsupported value.");
            rules.Add(new MaintenanceRule { ResourceId = resource, Target = db.Amount(stmt, 1), LegacyBuildingId = building,
                BuildingType = LegacyBuildingCatalog.Resolve(building), BuildingLimit = limit, Enabled = enabled == 1, Mode = (MaintenanceMode)mode });
        });
        db.Rows("SELECT BLD_ID, CAPACITY, ENABLED FROM BLD_CAPACITY_TABLE", 3, stmt =>
        {
            if (capacities.Count >= MaxRows) throw new InvalidDataException("There are too many capacity rules.");
            int building = db.Integer(stmt, 0), enabled = db.Integer(stmt, 2);
            if (building <= 0 || enabled is < 0 or > 1) throw new InvalidDataException("An original capacity rule contains an unsupported value.");
            capacities.Add(new CapacityRule { LegacyBuildingId = building, BuildingType = LegacyBuildingCatalog.Resolve(building),
                Capacity = db.Amount(stmt, 1), Enabled = enabled == 1 });
        });
        return new(Convert.ToHexString(SHA256.HashData(bytes)), rules, capacities);
    }

    private sealed class SnapshotDb : IDisposable
    {
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string name, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_deserialize(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string schema, IntPtr data, long length, long capacity, uint flags);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_close_v2(IntPtr db);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr stmt, IntPtr tail);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_step(IntPtr stmt);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_count(IntPtr stmt);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_type(IntPtr stmt, int column);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern long sqlite3_column_int64(IntPtr stmt, int column);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern double sqlite3_column_double(IntPtr stmt, int column);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_text(IntPtr stmt, int column);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_errmsg(IntPtr db);
        private IntPtr _db;
        private GCHandle _buffer;
        public SnapshotDb(byte[] bytes)
        {
            try
            {
                int opened = sqlite3_open_v2(":memory:", out _db, 0x2 | 0x4, IntPtr.Zero);
                if (opened != 0) throw Error();
                _buffer = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                if (sqlite3_deserialize(_db, "main", _buffer.AddrOfPinnedObject(), bytes.Length, bytes.Length, 4) != 0) throw Error();
            }
            catch { Dispose(); throw; }
        }
        private InvalidDataException Error() => new("Could not read original preferences: " + (_db == IntPtr.Zero ? "SQLite unavailable" : Marshal.PtrToStringUTF8(sqlite3_errmsg(_db))));
        public void Rows(string sql, int columns, Action<IntPtr> accept)
        {
            if (sqlite3_prepare_v2(_db, sql, -1, out var stmt, IntPtr.Zero) != 0) throw Error();
            try
            {
                if (sqlite3_column_count(stmt) != columns) throw new InvalidDataException("Preference database columns do not match.");
                int code;
                while ((code = sqlite3_step(stmt)) == 100) accept(stmt);
                if (code != 101) throw Error();
            }
            finally { sqlite3_finalize(stmt); }
        }
        public int Integer(IntPtr stmt, int col)
        {
            if (sqlite3_column_type(stmt, col) != 1) throw new InvalidDataException("Expected an integer in original preferences.");
            long n = sqlite3_column_int64(stmt, col);
            if (n < int.MinValue || n > int.MaxValue) throw new InvalidDataException("An integer in original preferences is out of range.");
            return (int)n;
        }
        public decimal Amount(IntPtr stmt, int col)
        {
            if (sqlite3_column_type(stmt, col) is not (1 or 2)) throw new InvalidDataException("Expected a number in original preferences.");
            double value = sqlite3_column_double(stmt, col);
            if (!double.IsFinite(value) || value < 0 || value > 1000000000) throw new InvalidDataException("An amount in original preferences is out of range.");
            return (decimal)value;
        }
        public string ScalarText(string sql)
        {
            string? result = null; int count = 0;
            Rows(sql, 1, stmt => { count++; result = Marshal.PtrToStringUTF8(sqlite3_column_text(stmt, 0)); });
            return count == 1 ? result ?? "" : "";
        }
        public void RequireOrdinaryTables()
        {
            var tables = new HashSet<string>(StringComparer.Ordinal);
            Rows("SELECT name, sql FROM sqlite_master WHERE type = 'table' AND name IN ('RES_MAINTAINED_TABLE','BLD_CAPACITY_TABLE')", 2, stmt =>
            {
                string name = Marshal.PtrToStringUTF8(sqlite3_column_text(stmt, 0)) ?? "";
                string sql = Marshal.PtrToStringUTF8(sqlite3_column_text(stmt, 1)) ?? "";
                if (!sql.TrimStart().StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase) || !tables.Add(name))
                    throw new InvalidDataException("Original preferences do not contain ordinary tables.");
            });
            if (tables.Count != 2) throw new InvalidDataException("Original preferences are missing expected tables.");
        }
        public void Dispose()
        {
            if (_db != IntPtr.Zero) { sqlite3_close_v2(_db); _db = IntPtr.Zero; }
            if (_buffer.IsAllocated) _buffer.Free();
        }
    }
}
