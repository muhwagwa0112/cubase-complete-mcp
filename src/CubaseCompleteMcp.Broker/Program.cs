using CubaseCompleteMcp.Broker;
using CubaseCompleteMcp.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using var mutex = new Mutex(true, @"Local\CubaseCompleteMcp.Broker.v1", out var ownsMutex);
if (!ownsMutex)
{
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton(CubasePaths.Discover());
builder.Services.AddSingleton(BuildIdentityService.Current());
builder.Services.AddSingleton<ConfirmationStore>();
builder.Services.AddSingleton<CubaseProcessInspector>();
builder.Services.AddSingleton<WinMmMidiTransport>();
builder.Services.AddSingleton<LoopMidiRuntime>();
builder.Services.AddSingleton<CubaseMidiSession>();
builder.Services.AddSingleton<CubaseUiAutomation>();
builder.Services.AddSingleton(provider => new MidiCompositionService(
    provider.GetRequiredService<CubasePaths>().LocalStateRoot));
builder.Services.AddSingleton<CubaseController>();
builder.Services.AddHostedService<BrokerPipeService>();

await builder.Build().RunAsync();
