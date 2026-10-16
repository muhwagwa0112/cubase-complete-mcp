using System.Diagnostics;
using System.IO.Pipes;

namespace CubaseCompleteMcp.Core;

/// <summary>
/// Single-connection pipe client. A request moves through <c>NotSent → Sending → WaitingForResponse → Completed</c>.
/// Once the first byte may have left, a lost connection, timeout or cancellation makes the outcome of a write
/// unknowable, so writes are never re-sent: they return <see cref="ErrorCodes.OutcomeUnknown"/>. Only stateless
/// diagnostic reads get one retry on a fresh connection, because every other request depends on the broker-side
/// client session (confirmations, target handles, compositions) that belonged to the lost connection.
/// </summary>
public sealed class BrokerClient : IAsyncDisposable
{
    public enum RequestPhase { NotSent, Sending, WaitingForResponse, Completed }

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly BuildIdentity _identity;
    private readonly Func<CancellationToken, Task<Stream>> _connect;
    private Stream? _stream;

    public BrokerClient() : this(ConnectToBrokerAsync, BuildIdentityService.Current())
    {
    }

    public BrokerClient(Func<CancellationToken, Task<Stream>> connect, BuildIdentity identity)
    {
        _connect = connect;
        _identity = identity;
    }

    /// <summary>Incremented whenever a new pipe connection (and therefore a new broker client session) is opened.</summary>
    public long ConnectionGeneration { get; private set; }

    public RequestPhase LastRequestPhase { get; private set; }

    public async Task<BrokerResponse> SendAsync(BrokerRequest request, CancellationToken cancellationToken = default)
    {
        request = request with
        {
            ClientProtocolVersion = request.ClientProtocolVersion ?? _identity.PipeProtocolVersion,
            ClientReleaseId = request.ClientReleaseId ?? _identity.ReleaseId
        };
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var first = await TrySendOnceAsync(request, cancellationToken).ConfigureAwait(false);
            if (first.Response is not null) return first.Response;
            var error = first.Error!;
            if (first.Phase == RequestPhase.NotSent)
                return NotDispatched(request, error);
            if (OperationReplayPolicy.IsStatelessDiagnosticRead(request) && !cancellationToken.IsCancellationRequested)
            {
                var retry = await TrySendOnceAsync(request, cancellationToken).ConfigureAwait(false);
                if (retry.Response is not null) return retry.Response;
                return BrokerResponse.Failure(request.Id, ResultStatus.Error, "BROKER_UNAVAILABLE",
                    $"The broker connection was lost twice while reading '{request.Operation}': {retry.Error!.Message}",
                    executionState: ExecutionState.NotApplicable, retryable: true);
            }
            if (OperationReplayPolicy.IsProvablyReadOnly(request))
                return BrokerResponse.Failure(request.Id, ResultStatus.Error, ErrorCodes.ClientSessionLost,
                    $"The broker connection was lost while reading '{request.Operation}'. Session-scoped reads are not re-sent on a new connection because targets, confirmations and documents belonged to the lost session.",
                    executionState: ExecutionState.NotStarted);
            return BrokerResponse.OutcomeUnknown(request.Id,
                $"The broker connection was lost after '{request.Operation}' was sent ({error.GetType().Name}: {error.Message}). The change was not re-sent.",
                path: "broker_pipe") with { OperationId = request.OperationId };
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(BrokerResponse? Response, Exception? Error, RequestPhase Phase)> TrySendOnceAsync(
        BrokerRequest request, CancellationToken cancellationToken)
    {
        LastRequestPhase = RequestPhase.NotSent;
        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            LastRequestPhase = RequestPhase.Sending; // before any byte can leave
            await PipeProtocol.WriteAsync(_stream!, request, cancellationToken).ConfigureAwait(false);
            LastRequestPhase = RequestPhase.WaitingForResponse;
            var response = await PipeProtocol.ReadAsync<BrokerResponse>(_stream!, cancellationToken).ConfigureAwait(false)
                ?? throw new EndOfStreamException("Cubase broker closed the connection without a response.");
            if (!string.Equals(response.Id, request.Id, StringComparison.Ordinal))
                throw new InvalidDataException($"Broker response id '{response.Id}' does not match request '{request.Id}'.");
            LastRequestPhase = RequestPhase.Completed;
            return (response, null, RequestPhase.Completed);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Any failure leaves the pipe at an undefined position (a late response could be read as the answer
            // to the next request), so the connection is always discarded.
            var phase = LastRequestPhase;
            await ResetAsync().ConfigureAwait(false);
            return (null, error, phase);
        }
    }

    private static BrokerResponse NotDispatched(BrokerRequest request, Exception error) =>
        error is OperationCanceledException
            ? BrokerResponse.Failure(request.Id, ResultStatus.Error, ErrorCodes.Cancelled,
                "The request was cancelled before it was sent to the broker.") with { OperationId = request.OperationId }
            : BrokerResponse.Failure(request.Id, ResultStatus.Offline, "BROKER_UNAVAILABLE",
                $"The Cubase broker could not be reached; nothing was sent. {error.Message}", retryable: true)
                with { OperationId = request.OperationId };

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_stream is NamedPipeClientStream { IsConnected: true } || _stream is not null and not NamedPipeClientStream)
            return;
        await ResetAsync().ConfigureAwait(false);
        _stream = await _connect(cancellationToken).ConfigureAwait(false);
        ConnectionGeneration++;
    }

    private static async Task<Stream> ConnectToBrokerAsync(CancellationToken cancellationToken)
    {
        var pipe = CreatePipe();
        try
        {
            await pipe.ConnectAsync(300, cancellationToken).ConfigureAwait(false);
            return pipe;
        }
        catch (TimeoutException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }

        StartBroker();
        var deadline = Stopwatch.StartNew();
        Exception? lastError = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(8))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = CreatePipe();
            try
            {
                await candidate.ConnectAsync(500, cancellationToken).ConfigureAwait(false);
                return candidate;
            }
            catch (Exception error) when (error is TimeoutException or IOException)
            {
                lastError = error;
                await candidate.DisposeAsync().ConfigureAwait(false);
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IOException("Cubase broker did not become available within 8 seconds.", lastError);
    }

    private static NamedPipeClientStream CreatePipe() => new(".", CubaseConstants.PipeName,
        PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.WriteThrough);

    private static void StartBroker()
    {
        var explicitPath = Environment.GetEnvironmentVariable("CUBASE_COMPLETE_MCP_BROKER");
        var baseDirectory = AppContext.BaseDirectory;
        var executable = explicitPath ?? Path.Combine(baseDirectory, "CubaseCompleteMcp.Broker.exe");
        var dll = Path.Combine(baseDirectory, "CubaseCompleteMcp.Broker.dll");

        ProcessStartInfo startInfo;
        if (File.Exists(executable))
        {
            startInfo = new ProcessStartInfo(executable);
        }
        else if (File.Exists(dll))
        {
            startInfo = new ProcessStartInfo("dotnet", $"\"{dll}\"");
        }
        else
        {
            throw new FileNotFoundException(
                "Cubase broker executable was not found beside the MCP server. Set CUBASE_COMPLETE_MCP_BROKER to its path.",
                executable);
        }

        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.WindowStyle = ProcessWindowStyle.Hidden;
        Process.Start(startInfo)?.Dispose();
    }

    private async Task ResetAsync()
    {
        if (_stream is not null)
        {
            try { await _stream.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { }
            _stream = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ResetAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
