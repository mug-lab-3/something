using Something.Application.Memory;

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

    private void Release(Guid id)
    {
        lock (sync)
        {
            if (reservations.Remove(id))
                Signal();
        }
    }

    private T Charge<T>(Guid id, ulong bufferBytes, Func<AvailableMemory?, MemoryChoice<T>> decide)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(!reservations.ContainsKey(id), typeof(Reservation));
            var choice = decide(Subtract(readAvailable(), TotalExcept(id)));
            Hold(id, bufferBytes, choice.Bytes);
            return choice.Value;
        }
    }

    // The reservation never shrinks: it holds the buffer and the largest charge so far.
    private void Hold(Guid id, ulong bufferBytes, ulong bytes)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(!reservations.ContainsKey(id), typeof(Reservation));
            if (bytes == 0)
                return;
            reservations[id] = Math.Max(reservations[id], Saturate((UInt128)bufferBytes + bytes));
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

    /// <summary>The memory one scan or search holds until disposed.</summary>
    public sealed class Reservation : IMemoryAllowance, IDisposable
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

        public T Charge<T>(Func<AvailableMemory?, MemoryChoice<T>> decide) =>
            owner.Charge(id, bufferBytes, decide);

        public void Hold(ulong bytes) => owner.Hold(id, bufferBytes, bytes);

        public void Dispose() => owner.Release(id);
    }
}
