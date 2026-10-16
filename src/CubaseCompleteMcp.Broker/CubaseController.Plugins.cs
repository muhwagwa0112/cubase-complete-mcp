using System.Text.Json.Nodes;
using CubaseCompleteMcp.Core;

namespace CubaseCompleteMcp.Broker;

/// <summary>
/// Installed plug-in catalog and slot read-back. MIDI Remote API 1.3 lists the plug-ins a slot can hold with their class
/// UIDs, and a loaded plug-in appears as the slot's child object whose unique name is "&lt;UID&gt;-&lt;instance&gt;". Hosts
/// without that API fall back to the configured <see cref="IPluginCatalogProvider"/>.
/// </summary>
public sealed partial class CubaseController
{
    private const int PluginCatalogPage = 200;
    private const int PluginCatalogLimit = 5000;

    private bool DriverListsPlugins => ReadBool((_driver.Hello?.Capabilities as JsonObject)?["plugin_catalog"]) == true;

    /// <summary>Hex digits of a plug-in class UID in upper case, so host spellings with braces or dashes compare equal.</summary>
    internal static string NormalizePluginUid(string uid) => new string(uid.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

    private async Task<PluginCatalogSnapshot> PluginCatalogAsync(TargetIdentity? target, long? slotObjectId, CancellationToken cancellationToken)
    {
        if (!DriverListsPlugins) return await _plugins.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        const string source = "midi_remote_api_1_3_plugin_manager";
        var entries = new List<PluginCatalogEntry>();
        var offset = 0;
        while (offset < PluginCatalogLimit)
        {
            var args = target is null ? new JsonObject() : TargetArguments(target);
            args["action"] = "catalog";
            if (slotObjectId is { } slot) args["object_id"] = slot;
            args["offset"] = offset;
            args["limit"] = PluginCatalogPage;
            var page = await ReadDriverAsync("plugin.get", args, cancellationToken).ConfigureAwait(false);
            if (ReadBool(page["available"]) != true)
                return new PluginCatalogSnapshot(false, source, [], page["reason"]?.GetValue<string>() ?? ErrorCodes.PluginCatalogUnavailable);
            var items = page["entries"] as JsonArray ?? [];
            foreach (var item in items.OfType<JsonObject>())
            {
                var uid = item["uid"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(uid) || NormalizePluginUid(uid).Length != 32) continue;
                entries.Add(new PluginCatalogEntry(uid, item["name"]?.GetValue<string>() ?? uid, item["vendor"]?.GetValue<string>(),
                    item["sub_categories"]?.GetValue<string>(), true));
            }
            offset += items.Count;
            if (items.Count == 0 || offset >= (ReadLong(page["total"]) ?? 0)) break;
        }
        return new PluginCatalogSnapshot(true, source, entries, null);
    }

    private async Task<string?> ObserveSlotPluginUidAsync(TargetIdentity target, long slotObjectId, CancellationToken cancellationToken)
    {
        if (!DriverListsPlugins) return await _plugins.ObserveSlotPluginUidAsync(target, slotObjectId, cancellationToken).ConfigureAwait(false);
        var args = TargetArguments(target);
        args["action"] = "inspect_slot";
        args["object_id"] = slotObjectId;
        var slot = await ReadDriverAsync("plugin.get", args, cancellationToken).ConfigureAwait(false);
        return slot["slot"]?["loaded"]?["plugin_uid"]?.GetValue<string>() is { } uid ? NormalizePluginUid(uid) : null;
    }
}
