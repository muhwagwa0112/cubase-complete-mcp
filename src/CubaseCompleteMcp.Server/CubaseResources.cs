using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using CubaseCompleteMcp.Core;
using ModelContextProtocol.Server;

namespace CubaseCompleteMcp.Server;

[McpServerResourceType]
public sealed class CubaseResources(BrokerClient broker)
{
    [McpServerResource(UriTemplate = "cubase://state", Name = "cubase-state", MimeType = "application/json")]
    [Description("Current Cubase, broker, driver and project state.")]
    public Task<string> State(CancellationToken cancellationToken = default) => Read("status", null, cancellationToken);

    [McpServerResource(UriTemplate = "cubase://capabilities", Name = "cubase-capabilities", MimeType = "application/json")]
    [Description("Capability matrix with paths, risks, prerequisites and verification state.")]
    public Task<string> Capabilities(CancellationToken cancellationToken = default) =>
        Read("capabilities.search", new JsonObject { ["limit"] = 500 }, cancellationToken);

    [McpServerResource(UriTemplate = "cubase://commands", Name = "cubase-commands", MimeType = "application/json")]
    [Description("Extracted Cubase Key Command catalog.")]
    public Task<string> Commands(CancellationToken cancellationToken = default) =>
        Read("commands.list", null, cancellationToken);

    [McpServerResource(UriTemplate = "cubase://events", Name = "cubase-events", MimeType = "application/json")]
    [Description("Last observed driver state events and session revision.")]
    public Task<string> Events(CancellationToken cancellationToken = default) => Read("diagnostics", null, cancellationToken);

    private async Task<string> Read(string operation, JsonObject? arguments, CancellationToken cancellationToken)
    {
        var result = await broker.SendAsync(new BrokerRequest(Guid.NewGuid().ToString("N"), operation, arguments),
            cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(result, PipeProtocol.JsonOptions);
    }
}
