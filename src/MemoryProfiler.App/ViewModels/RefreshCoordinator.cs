namespace MemoryProfiler.App.ViewModels;

internal sealed class RefreshCoordinator : IDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private RefreshLease? _current;
    private bool _isDisposed;

    public async ValueTask<RefreshLease> BeginAsync(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        RefreshLease? previous;
        RefreshLease lease;

        await _transitionGate.WaitAsync();
        try
        {
            lock (_sync)
            {
                if (_isDisposed)
                {
                    source.Dispose();
                    throw new ObjectDisposedException(nameof(RefreshCoordinator));
                }

                previous = _current;
                if (previous is not null && !previous.TryAcquireReference())
                {
                    source.Dispose();
                    throw new InvalidOperationException(
                        "The active refresh lease was already retired.");
                }

                lease = new RefreshLease(this, source);
                _current = lease;
            }
        }
        finally
        {
            _transitionGate.Release();
        }

        previous?.RequestCancellation();
        return lease;
    }

    public void Dispose()
    {
        RefreshLease? current;

        _transitionGate.Wait();
        try
        {
            lock (_sync)
            {
                if (_isDisposed)
                {
                    return;
                }

                _isDisposed = true;
                current = _current;
                if (current is not null && !current.TryAcquireReference())
                {
                    current = null;
                }

                _current = null;
            }
        }
        finally
        {
            _transitionGate.Release();
        }

        current?.RequestCancellation();
    }

    internal bool IsCurrent(RefreshLease lease)
    {
        lock (_sync)
        {
            return !_isDisposed && ReferenceEquals(_current, lease);
        }
    }

    internal async ValueTask<bool> TryRunIfCurrentAsync(
        RefreshLease lease,
        Action action)
    {
        await _transitionGate.WaitAsync();
        try
        {
            lock (_sync)
            {
                if (_isDisposed || !ReferenceEquals(_current, lease))
                {
                    return false;
                }
            }

            action();
            return true;
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    internal void Retire(RefreshLease lease)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_current, lease))
            {
                _current = null;
            }
        }

        lease.ReleaseReference();
    }
}

internal sealed class RefreshLease : IDisposable
{
    private readonly RefreshCoordinator _owner;
    private readonly CancellationTokenSource _source;
    private readonly CancellationToken _token;
    private int _referenceCount = 1;
    private int _isRetired;
    private int _isCancellationRequested;

    public RefreshLease(
        RefreshCoordinator owner,
        CancellationTokenSource source)
    {
        _owner = owner;
        _source = source;
        _token = source.Token;
    }

    public CancellationToken Token => _token;

    public bool IsCurrent => _owner.IsCurrent(this);

    public ValueTask<bool> TryRunIfCurrentAsync(Action action) =>
        _owner.TryRunIfCurrentAsync(this, action);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isRetired, 1) == 0)
        {
            _owner.Retire(this);
        }
    }

    internal bool TryAcquireReference()
    {
        while (true)
        {
            var referenceCount = Volatile.Read(ref _referenceCount);
            if (referenceCount == 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(
                    ref _referenceCount,
                    referenceCount + 1,
                    referenceCount) == referenceCount)
            {
                return true;
            }
        }
    }

    internal void RequestCancellation()
    {
        if (Interlocked.Exchange(ref _isCancellationRequested, 1) != 0)
        {
            ReleaseReference();
            return;
        }

        try
        {
            _ = CompleteCancellationAsync(_source.CancelAsync());
        }
        catch (ObjectDisposedException)
        {
            ReleaseReference();
        }
    }

    internal void ReleaseReference()
    {
        if (Interlocked.Decrement(ref _referenceCount) == 0)
        {
            _source.Dispose();
        }
    }

    private async Task CompleteCancellationAsync(Task cancellation)
    {
        try
        {
            await cancellation.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A cancellation callback failure must not block a replacement refresh.
        }
        finally
        {
            ReleaseReference();
        }
    }
}
