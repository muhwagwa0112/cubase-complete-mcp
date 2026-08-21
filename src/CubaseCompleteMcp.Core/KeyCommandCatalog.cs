using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace CubaseCompleteMcp.Core;

public sealed class KeyCommandCatalog
{
    private readonly IReadOnlyList<KeyCommandDescriptor> _commands;
    private readonly IReadOnlyDictionary<string, KeyCommandDescriptor> _byId;
    private readonly IReadOnlyDictionary<string, KeyCommandDescriptor> _byQualifiedName;

    public KeyCommandCatalog(IEnumerable<KeyCommandDescriptor> commands)
    {
        _commands = commands.OrderBy(static command => command.Index).ToArray();
        _byId = _commands.ToDictionary(static command => command.Id, StringComparer.OrdinalIgnoreCase);
        _byQualifiedName = _commands.ToDictionary(
            static command => Qualified(command.Category, command.Name),
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<KeyCommandDescriptor> Commands => _commands;
    public int CategoryCount => _commands.Select(static command => command.Category).Distinct(StringComparer.Ordinal).Count();

    public static KeyCommandCatalog Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Cubase Key Commands.xml was not found.", path);
        }

        using var stream = File.OpenRead(path);
        var document = XDocument.Load(stream, LoadOptions.None);
        var categories = document.Root?
            .Elements("list")
            .SingleOrDefault(element => (string?)element.Attribute("name") == "Categories")
            ?? throw new InvalidDataException("Key Commands.xml does not contain the Categories list.");

        var output = new List<KeyCommandDescriptor>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var categoryNode in categories.Elements("item"))
        {
            var category = ReadNamedString(categoryNode, "Name");
            var commandList = categoryNode.Elements("list")
                .SingleOrDefault(element => (string?)element.Attribute("name") == "Commands");
            if (string.IsNullOrWhiteSpace(category) || commandList is null)
            {
                continue;
            }

            foreach (var commandNode in commandList.Elements("item"))
            {
                var name = ReadNamedString(commandNode, "Name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var qualified = Qualified(category, name);
                if (!seen.Add(qualified))
                {
                    throw new InvalidDataException($"Duplicate Cubase command: {category} / {name}.");
                }

                output.Add(new KeyCommandDescriptor(CreateStableId(category, name), category, name, output.Count));
            }
        }

        if (output.Count == 0)
        {
            throw new InvalidDataException("No Cubase key commands were parsed.");
        }

        return new KeyCommandCatalog(output);
    }

    public KeyCommandDescriptor? Find(string category, string name) =>
        _byQualifiedName.GetValueOrDefault(Qualified(category, name));

    public KeyCommandDescriptor? FindById(string id) => _byId.GetValueOrDefault(id);

    public IReadOnlyList<KeyCommandDescriptor> Search(string? query, string? category = null, int limit = 100)
    {
        limit = Math.Clamp(limit, 1, 500);
        IEnumerable<KeyCommandDescriptor> result = _commands;
        if (!string.IsNullOrWhiteSpace(category))
        {
            result = result.Where(command => command.Category.Contains(category, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            result = result.Where(command => command.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || command.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
                || command.Id.Equals(query, StringComparison.OrdinalIgnoreCase));
        }

        return result.Take(limit).ToArray();
    }

    public static string CreateStableId(string category, string name)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Qualified(category, name)));
        return "cmd-" + Convert.ToHexString(bytes.AsSpan(0, 10)).ToLowerInvariant();
    }

    private static string Qualified(string category, string name) => category + "\0" + name;

    private static string? ReadNamedString(XElement parent, string name) => parent.Elements("string")
        .SingleOrDefault(element => (string?)element.Attribute("name") == name)?
        .Attribute("value")?.Value;
}

