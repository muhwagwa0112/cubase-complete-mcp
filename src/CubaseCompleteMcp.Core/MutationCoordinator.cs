namespace CubaseCompleteMcp.Core;

public sealed record MutationLeaseRefusal(string ErrorCode, string Message);

/// <summary>
/// Serializes mutation workflows against one Cubase instance. A lease covers dispatch and postcondition
/// observation; reads never need it. The lease is re-entrant for the flow that holds it (a batch holds one lease
/// for all children and observers), which avoids waiting on itself. Waiting is bounded (5 s) and so is the queue
/// (32); beyond either the request is refused with BUSY before anything is sent. A lease does not stop a person
/// from operating Cubase by hand, so final target/project conditions are still re-checked under the lease.
/// </summary>
public sealed class MutationCoordinator(TimeSpan? maximumWait = null, int maximumQueue = 32)
{
    private readonly SemaphoreSlim _lease = new(1, 1);
    private readonly AsyncLocal<LeaseToken?> _held = new();
    private readonly TimeSpan _maximumWait = maximumWait ?? TimeSpan.FromSeconds(5);
    private int _waiting;
    private long _sequence;

    public int Waiting => Volatile.Read(ref _waiting);
    public bool IsHeldByCurrentFlow => _held.Value is { Released: false };
    public long LeasesGranted => Interlocked.Read(ref _sequence);

    /// <summary>
    /// Runs <paramref name="body"/> while holding the lease, or returns <paramref name="refused"/>'s result without
    /// running it when the lease cannot be obtained. A nested call from the holding flow runs immediately.
    /// </summary>
    public async Task<T> RunExclusiveAsync<T>(string description, Func<Task<T>> body,
        Func<MutationLeaseRefusal, T> refused, CancellationToken cancellationToken)
    {
        if (_held.Value is { Released: false }) return await body().ConfigureAwait(false);
        if (Interlocked.Increment(ref _waiting) > maximumQueue)
        {
            Interlocked.Decrement(ref _waiting);
            return refused(new MutationLeaseRefusal(ErrorCodes.Busy,
                $"More than {maximumQueue} Cubase changes are already queued; '{description}' was not sent."));
        }
        bool acquired;
        try
        {
            acquired = await _lease.WaitAsync(_maximumWait, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
        if (!acquired)
            return refused(new MutationLeaseRefusal(ErrorCodes.Busy,
                $"Another Cubase change held the mutation lease for more than {_maximumWait.TotalSeconds:0} s; '{description}' was not sent."));
        var token = new LeaseToken(Interlocked.Increment(ref _sequence), description);
        _held.Value = token;
        try
        {
            return await body().ConfigureAwait(false);
        }
        finally
        {
            token.Released = true;
            _held.Value = null;
            _lease.Release();
        }
    }

    private sealed class LeaseToken(long sequence, string description)
    {
        public bool Released { get; set; }
        public long Sequence { get; } = sequence;
        public string Description { get; } = description;
    }
}
