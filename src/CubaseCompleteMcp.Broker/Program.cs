using System.IO;
using CubaseCompleteMcp.Broker;
using CubaseCompleteMcp.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Protocol 2 uses its own mutex and pipe name, so a running protocol-1 broker is neither reused nor terminated.
using var mutex = new Mutex(true, CubaseConstants.BrokerMutexName, out var ownsMutex);
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
builder.Services.AddSingleton<IHostProcessProbe>(provider => provider.GetRequiredService<CubaseProcessInspector>());
builder.Services.AddSingleton<WinMmMidiTransport>();
builder.Services.AddSingleton<LoopMidiRuntime>();
builder.Services.AddSingleton<CubaseMidiSession>();
builder.Services.AddSingleton<ICubaseDriver>(provider => provider.GetRequiredService<CubaseMidiSession>());
builder.Services.AddSingleton<CubaseUiAutomation>();
builder.Services.AddSingleton<IWorkflowUi>(provider => provider.GetRequiredService<CubaseUiAutomation>());
builder.Services.AddSingleton<IPluginCatalogProvider, UnavailablePluginCatalogProvider>();
builder.Services.AddSingleton(provider => new MidiCompositionService(
    provider.GetRequiredService<CubasePaths>().LocalStateRoot));
builder.Services.AddSingleton(provider => new ExecutionJournal(
    Path.Combine(provider.GetRequiredService<CubasePaths>().LocalStateRoot, "journal"),
    provider.GetRequiredService<BuildIdentity>().GitSha));
builder.Services.AddSingleton(_ => new TargetHandleStore());
builder.Services.AddSingleton(_ => new MutationCoordinator());
builder.Services.AddSingleton(provider => new CubaseController(
    provider.GetRequiredService<CubasePaths>(), provider.GetRequiredService<ConfirmationStore>(),
    provider.GetRequiredService<IHostProcessProbe>(), provider.GetRequiredService<ICubaseDriver>(),
    provider.GetRequiredService<IWorkflowUi>(), provider.GetRequiredService<MidiCompositionService>(),
    provider.GetRequiredService<BuildIdentity>(), provider.GetRequiredService<ExecutionJournal>(),
    provider.GetRequiredService<TargetHandleStore>(), provider.GetRequiredService<MutationCoordinator>(),
    provider.GetRequiredService<IPluginCatalogProvider>()));
builder.Services.AddHostedService<BrokerPipeService>();

await builder.Build().RunAsync();
