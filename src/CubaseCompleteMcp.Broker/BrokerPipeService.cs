using System.IO;
using System.IO.Pipes;
using CubaseCompleteMcp.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CubaseCompleteMcp.Broker;

public sealed class BrokerPipeService(
    CubaseController controller,
    ILogger<BrokerPipeService> logger,
    IHostApplicationLifetime lifetime) : BackgroundService
{
    private long _lastActivityTicks = DateTimeOffset.UtcNow.UtcTicks;
    private int _activeClients;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Cubase broker listening on pipe {PipeName}", CubaseConstants.PipeName);
        _ = MonitorIdleAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                CubaseConstants.PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                Interlocked.Increment(ref _activeClients);
                MarkActivity();
                _ = HandleClientAsync(pipe, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch (Exception error)
            {
                logger.LogError(error, "Failed while accepting a Cubase broker client.");
                await pipe.DisposeAsync().ConfigureAwait(false);
                await Task.Delay(250, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        var clientSessionId = Guid.NewGuid().ToString("N");
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                while (pipe.IsConnected && !stoppingToken.IsCancellationRequested)
                {
                    var request = await PipeProtocol.ReadAsync<BrokerRequest>(pipe, stoppingToken).ConfigureAwait(false);
                    if (request is null)
                    {
                        return;
                    }
                    request = request with { ClientSessionId = clientSessionId };

                    MarkActivity();

                    BrokerResponse response;
                    try
                    {
                        response = await controller.HandleAsync(request, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception error)
                    {
                        logger.LogError(error, "Unhandled broker request error for {Operation}", request.Operation);
                        response = BrokerResponse.Failure(request.Id, ResultStatus.Error,
                            "INTERNAL_ERROR", "The Cubase broker could not complete the request.");
                    }

                    await PipeProtocol.WriteAsync(pipe, response, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is IOException or EndOfStreamException or OperationCanceledException)
            {
                logger.LogDebug(error, "Cubase broker client disconnected.");
            }
            finally
            {
                Interlocked.Decrement(ref _activeClients);
                MarkActivity();
            }
        }
    }

    private async Task MonitorIdleAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            var idle = DateTimeOffset.UtcNow - new DateTimeOffset(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);
            if (Volatile.Read(ref _activeClients) == 0 && idle >= TimeSpan.FromMinutes(5))
            {
                logger.LogInformation("Cubase broker is stopping after {IdleMinutes} idle minutes.", idle.TotalMinutes);
                lifetime.StopApplication();
                return;
            }
        }
    }

    private void MarkActivity() => Interlocked.Exchange(ref _lastActivityTicks, DateTimeOffset.UtcNow.UtcTicks);
}
