using System.Diagnostics;
using System.IO.Pipes;

namespace CubaseCompleteMcp.Core;

public sealed class BrokerClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly BuildIdentity _identity = BuildIdentityService.Current();
    private NamedPipeClientStream? _pipe;

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
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await PipeProtocol.WriteAsync(_pipe!, request, cancellationToken).ConfigureAwait(false);
                return await PipeProtocol.ReadAsync<BrokerResponse>(_pipe!, cancellationToken).ConfigureAwait(false)
                    ?? throw new EndOfStreamException("Cubase broker closed the connection without a response.");
            }
            catch (IOException)
            {
                await ResetAsync().ConfigureAwait(false);
                await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                await PipeProtocol.WriteAsync(_pipe!, request, cancellationToken).ConfigureAwait(false);
                return await PipeProtocol.ReadAsync<BrokerResponse>(_pipe!, cancellationToken).ConfigureAwait(false)
                    ?? throw new EndOfStreamException("Cubase broker closed the retried connection without a response.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_pipe?.IsConnected == true)
        {
            return;
        }

        _pipe = CreatePipe();
        try
        {
            await _pipe.ConnectAsync(300, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (TimeoutException)
        {
            await _pipe.DisposeAsync().ConfigureAwait(false);
            _pipe = null;
        }

        StartBroker();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8);
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = CreatePipe();
            try
            {
                await candidate.ConnectAsync(500, cancellationToken).ConfigureAwait(false);
                _pipe = candidate;
                return;
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
        if (_pipe is not null)
        {
            await _pipe.DisposeAsync().ConfigureAwait(false);
            _pipe = null;
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
