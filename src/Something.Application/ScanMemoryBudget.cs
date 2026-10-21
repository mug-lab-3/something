using Something.Application.Repositories;
using Something.Storage;

namespace Something.Application;

/// <summary>Coordinates future scan allocations across drives. Reservations are conservative
/// estimates, not a hard limit on the managed heap. Existing allocations may also be reflected
/// in the OS sample; subtracting the full competing budget deliberately errs toward file storage.</summary>
public sealed class ScanMemoryBudget(Func<AvailableMemory?> readAvailable)
{
    /// <summary>Current availability of this machine; null when it cannot be read.</summary>
    public static AvailableMemory? ReadSystem()
    {
        var memory = OperatingSystem.IsWindows()
            ? Windows.WindowsMemoryAvailability.TryRead()
            : null;
        return memory is null
            ? null
            : new(
                memory.TotalPhysicalBytes,
                memory.AvailablePhysicalBytes,
                memory.AvailableCommitBytes,
                memory.AvailableVirtualBytes
            );
    }

    private readonly object sync = new();
    private readonly Dictionary<Guid, ulong> reservations = [];
    private TaskCompletionSource changed = NewSignal();

    public async Task<Reservation> ReserveAsync(ulong bufferBytes, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfZero(bufferBytes);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Task wait;
            lock (sync)
            {
                var available = Subtract(readAvailable(), TotalExcept(Guid.Empty));
                ulong reserve = MemoryHeadroom.OsReserve(available);
                // A lone scan keeps the existing single-drive behavior, even when its
                // estimated buffer exceeds headroom or memory information is unavailable.
                if (reservations.Count == 0 || MemoryHeadroom.Fits(available, bufferBytes, reserve))
                {
                    var id = Guid.NewGuid();
                    reservations.Add(id, bufferBytes);
                    return new(this, id, bufferBytes);
                }
                wait = changed.Task;
            }
            await wait.WaitAsync(token);
        }
    }

    private DiagnosticDatabaseSelection Select(
        Guid id,
        ulong bufferBytes,
        ulong estimate,
        DiagnosticDatabasePreference preference,
        bool duckDb = false
    )
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(!reservations.ContainsKey(id), typeof(Reservation));
            var available = Subtract(readAvailable(), TotalExcept(id));
            var selection = duckDb
                ? DiagnosticDatabaseSelector.SelectDuckDb(estimate, available, preference)
                : DiagnosticDatabaseSelector.Select(estimate, available, preference);
            ulong databaseBudget = Saturate(
                (UInt128)(selection.UseMemory ? estimate : selection.FileCacheBytes) * 2
            );
            reservations[id] = Saturate((UInt128)bufferBytes + databaseBudget);
            Signal();
            return selection;
        }
    }

    private void Release(Guid id)
    {
        lock (sync)
        {
            if (reservations.Remove(id))
                Signal();
        }
    }

    private ulong? TryGrowDuckDatabase(Guid id, ulong bufferBytes, ulong currentLimit)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(!reservations.ContainsKey(id), typeof(Reservation));
            var available = Subtract(readAvailable(), TotalExcept(id));
            if (available is null)
                return null;
            ulong next = MemoryHeadroom.Align(
                (UInt128)currentLimit + MemoryHeadroom.Surplus(available) / 2
            );
            if (next <= currentLimit)
                return null;
            // OS availability already charges the live buffer and existing native allocations.
            // Budget twice the incremental growth for native overhead, not all existing RAM again.
            ulong additional = Saturate((UInt128)(next - currentLimit) * 2);
            if (!MemoryHeadroom.Fits(available, additional, MemoryHeadroom.OsReserve(available)))
                return null;
            Account(id, bufferBytes, next);
            return next;
        }
    }

    // The same limit a publication selects, from what remains after other reservations.
    private ulong SizeDuckDatabase(Guid id, ulong bufferBytes)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(!reservations.ContainsKey(id), typeof(Reservation));
            ulong limit = MemoryHeadroom.DuckLimit(Subtract(readAvailable(), TotalExcept(id)));
            Account(id, bufferBytes, limit);
            return limit;
        }
    }

    private void AccountDuckDatabaseLimit(Guid id, ulong bufferBytes, ulong limit)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(!reservations.ContainsKey(id), typeof(Reservation));
            Account(id, bufferBytes, limit);
        }
    }

    // A DuckDB limit is charged twice: the engine plus native allocations outside its limit.
    private void Account(Guid id, ulong bufferBytes, ulong limit)
    {
        reservations[id] = Math.Max(
            reservations[id],
            Saturate((UInt128)bufferBytes + (UInt128)limit * 2)
        );
        Signal();
    }

    private ulong TotalExcept(Guid id)
    {
        UInt128 total = 0;
        foreach (var pair in reservations)
            if (pair.Key != id)
                total += pair.Value;
        return Saturate(total);
    }

    private void Signal()
    {
        var previous = changed;
        changed = NewSignal();
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static ulong Saturate(UInt128 value) =>
        value > ulong.MaxValue ? ulong.MaxValue : (ulong)value;

    private static AvailableMemory? Subtract(AvailableMemory? memory, ulong reserved) =>
        MemoryHeadroom.Validate(memory) is { } valid
            ? valid with
            {
                PhysicalBytes = Remaining(valid.PhysicalBytes, reserved),
                CommitBytes = Remaining(valid.CommitBytes, reserved),
                VirtualBytes = Remaining(valid.VirtualBytes, reserved),
            }
            : null;

    private static ulong Remaining(ulong bytes, ulong reserved) =>
        bytes > reserved ? bytes - reserved : 0;

    public sealed class Reservation : IDisposable
    {
        private readonly ScanMemoryBudget owner;
        private readonly Guid id;
        private readonly ulong bufferBytes;

        internal Reservation(ScanMemoryBudget owner, Guid id, ulong bufferBytes)
        {
            this.owner = owner;
            this.id = id;
            this.bufferBytes = bufferBytes;
        }

        public DiagnosticDatabaseSelection SelectDatabase(
            ulong estimate,
            DiagnosticDatabasePreference preference = DiagnosticDatabasePreference.Automatic
        ) => owner.Select(id, bufferBytes, estimate, preference);

        public DiagnosticDatabaseSelection SelectDuckDatabase(ulong estimate) =>
            owner.Select(
                id,
                bufferBytes,
                estimate,
                DiagnosticDatabasePreference.File,
                duckDb: true
            );

        public void Dispose() => owner.Release(id);

        public ulong? TryGrowDuckDatabase(ulong currentLimit) =>
            owner.TryGrowDuckDatabase(id, bufferBytes, currentLimit);

        /// <summary>An initial DuckDB limit from the memory available now, at least 1 GiB.</summary>
        public ulong SizeDuckDatabase() => owner.SizeDuckDatabase(id, bufferBytes);

        public void AccountDuckDatabaseLimit(ulong limit) =>
            owner.AccountDuckDatabaseLimit(id, bufferBytes, limit);

        /// <summary>Lets a database operation grow its limit within this reservation.</summary>
        public DatabaseMemoryGrowth DuckDatabaseGrowth =>
            new(TryGrowDuckDatabase, AccountDuckDatabaseLimit);
    }
}
