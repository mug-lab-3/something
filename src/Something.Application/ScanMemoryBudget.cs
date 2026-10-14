using Something.Storage;

namespace Something.Application;

/// <summary>Coordinates future scan allocations across drives. Reservations are conservative
/// estimates, not a hard limit on the managed heap. Existing allocations may also be reflected
/// in the OS sample; subtracting the full competing budget deliberately errs toward file storage.</summary>
public sealed class ScanMemoryBudget(Func<AvailableMemory?> readAvailable)
{
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
                ulong reserve = available is null
                    ? 1024UL * 1024 * 1024
                    : Math.Clamp(available.TotalPhysicalBytes / 5, 1UL << 30, 4UL << 30);
                // A lone scan keeps the existing single-drive behavior, even when its
                // estimated buffer exceeds headroom or memory information is unavailable.
                if (reservations.Count == 0 || Fits(available, bufferBytes, reserve))
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
        memory is null
        || memory.TotalPhysicalBytes == 0
        || memory.PhysicalBytes > memory.TotalPhysicalBytes
            ? null
            : memory with
            {
                PhysicalBytes = Remaining(memory.PhysicalBytes, reserved),
                CommitBytes = Remaining(memory.CommitBytes, reserved),
                VirtualBytes = Remaining(memory.VirtualBytes, reserved),
            };

    private static ulong Remaining(ulong bytes, ulong reserved) =>
        bytes > reserved ? bytes - reserved : 0;

    private static bool Fits(AvailableMemory? memory, ulong bytes, ulong reserve) =>
        memory is not null
        && (UInt128)bytes + reserve <= memory.PhysicalBytes
        && (UInt128)bytes + reserve <= memory.CommitBytes
        && (UInt128)bytes + reserve <= memory.VirtualBytes;

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
    }
}
