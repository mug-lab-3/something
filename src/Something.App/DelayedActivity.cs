using Microsoft.UI.Xaml;

namespace Something.App;

// Busy immediately, visible only after the threshold. All leases share one indicator.
internal sealed class DelayedActivity(Action<bool, bool> present) : IDisposable
{
    private readonly HashSet<Lease> leases = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool subscribed;

    private sealed class Lease(DelayedActivity owner) : IDisposable
    {
        public void Dispose() => owner.End(this);
    }

    internal IDisposable Begin()
    {
        var lease = new Lease(this);
        leases.Add(lease);
        if (leases.Count == 1)
        {
            if (!subscribed)
            {
                timer.Tick += OnThreshold;
                subscribed = true;
            }
            present(true, false);
            timer.Start();
        }
        return lease;
    }

    private void OnThreshold(object? sender, object args)
    {
        timer.Stop();
        if (leases.Count == 0)
            return;
        present(true, true);
    }

    private void End(Lease lease)
    {
        if (!leases.Remove(lease) || leases.Count != 0)
            return;
        Dispose();
    }

    public void Dispose()
    {
        timer.Stop();
        leases.Clear();
        present(false, false);
    }
}
