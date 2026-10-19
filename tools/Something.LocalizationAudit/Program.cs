using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

if (args.Length != 2 || args[0] is not ("extract" or "check"))
    throw new ArgumentException(
        "Usage: Something.LocalizationAudit <extract|check> <repository-root>"
    );
string root = Path.GetFullPath(args[1]);
var entries = new List<SourceString>();
foreach (
    string file in Directory
        .EnumerateFiles(Path.Combine(root, "src"), "*", SearchOption.AllDirectories)
        .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
)
{
    string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
    if (Path.GetExtension(file) == ".cs")
    {
        var tree = CSharpSyntaxTree.ParseText(
            File.ReadAllText(file),
            new CSharpParseOptions(LanguageVersion.CSharp14)
        );
        var syntax = tree.GetRoot();
        foreach (
            var node in syntax
                .DescendantNodes()
                .Where(node =>
                    node is InterpolatedStringExpressionSyntax
                    || node is LiteralExpressionSyntax literal
                        && literal.IsKind(SyntaxKind.StringLiteralExpression)
                )
        )
        {
            // Nested expressions are separately inventoried: condition branches can contain UI words.
            bool interpolated = node is InterpolatedStringExpressionSyntax;
            string text;
            string[] arguments = [];
            if (node is InterpolatedStringExpressionSyntax format)
            {
                var builder = new StringBuilder();
                var expressions = new List<string>();
                foreach (var content in format.Contents)
                    if (content is InterpolatedStringTextSyntax part)
                        builder.Append(
                            part.TextToken.ValueText.Replace("{", "{{", StringComparison.Ordinal)
                                .Replace("}", "}}", StringComparison.Ordinal)
                        );
                    else if (content is InterpolationSyntax hole)
                    {
                        builder
                            .Append('{')
                            .Append(expressions.Count.ToString(CultureInfo.InvariantCulture));
                        if (hole.AlignmentClause is { } alignment)
                            builder.Append(',').Append(alignment.Value.ToString());
                        if (hole.FormatClause is { } specifier)
                            builder.Append(':').Append(specifier.FormatStringToken.ValueText);
                        builder.Append('}');
                        expressions.Add(hole.Expression.ToString());
                    }
                text = builder.ToString();
                arguments = [.. expressions];
            }
            else
                text = ((LiteralExpressionSyntax)node).Token.ValueText;
            bool diagnostic = node.Ancestors()
                .Take(4)
                .Any(parent =>
                    parent is ObjectCreationExpressionSyntax creation
                    && creation.Type.ToString().EndsWith("Exception", StringComparison.Ordinal)
                );
            var location = node.GetLocation().GetLineSpan().StartLinePosition;
            string member =
                node.Ancestors()
                    .OfType<MemberDeclarationSyntax>()
                    .Select(parent =>
                        parent switch
                        {
                            MethodDeclarationSyntax method => method.Identifier.ValueText,
                            PropertyDeclarationSyntax property => property.Identifier.ValueText,
                            ConstructorDeclarationSyntax constructor => constructor
                                .Identifier
                                .ValueText,
                            _ => null,
                        }
                    )
                    .FirstOrDefault(value => value is not null)
                ?? "";
            string context =
                node.Ancestors()
                    .FirstOrDefault(parent =>
                        parent is StatementSyntax or ArrowExpressionClauseSyntax or AttributeSyntax
                    )
                    ?.ToString()
                ?? node.Parent?.ToString()
                ?? "";
            if (
                !interpolated
                && context.Contains("XamlReader.Load", StringComparison.Ordinal)
                && text.TrimStart().StartsWith('<')
            )
            {
                var embedded = XDocument.Parse(text, LoadOptions.SetLineInfo);
                foreach (
                    var attribute in embedded
                        .Descendants()
                        .Attributes()
                        .Where(attribute =>
                            IsTextProperty(attribute.Name.LocalName)
                            && !attribute.Value.StartsWith('{')
                            && !string.IsNullOrWhiteSpace(attribute.Value)
                        )
                )
                {
                    var embeddedLocation = (IXmlLineInfo)attribute;
                    entries.Add(
                        new(
                            relative,
                            location.Line + 1 + embeddedLocation.LineNumber,
                            embeddedLocation.LinePosition,
                            "EmbeddedXaml",
                            member + "." + attribute.Name.LocalName,
                            attribute.Value,
                            attribute.Value,
                            [],
                            "Embedded preview XAML",
                            false
                        )
                    );
                }
            }
            entries.Add(
                new(
                    relative,
                    location.Line + 1,
                    location.Character + 1,
                    interpolated ? "CSharpFormat" : "CSharp",
                    member,
                    text,
                    node.ToString(),
                    arguments,
                    context,
                    diagnostic
                )
            );
        }
    }
    else if (Path.GetExtension(file) == ".xaml")
    {
        var document = XDocument.Load(file, LoadOptions.SetLineInfo);
        foreach (var element in document.Descendants())
        {
            foreach (
                var attribute in element
                    .Attributes()
                    .Where(attribute =>
                        IsTextProperty(attribute.Name.LocalName)
                        && !attribute.Value.StartsWith('{')
                        && !string.IsNullOrWhiteSpace(attribute.Value)
                    )
            )
            {
                var location = (IXmlLineInfo)attribute;
                string name =
                    (string?)
                        element.Attribute(
                            XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")
                        )
                    ?? element.Name.LocalName;
                entries.Add(
                    new(
                        relative,
                        location.LineNumber,
                        location.LinePosition,
                        "Xaml",
                        $"{name}.{attribute.Name.LocalName}",
                        attribute.Value,
                        attribute.Value,
                        [],
                        element.Name.LocalName,
                        false
                    )
                );
            }
            if (
                !element.HasElements
                && (
                    IsTextProperty(element.Name.LocalName)
                    || element.Name.LocalName is "TextBlock" or "Run" or "String"
                    || element.Name.LocalName == "Setter"
                        && IsTextProperty((string?)element.Attribute("Property") ?? "")
                )
                && !string.IsNullOrWhiteSpace(element.Value)
            )
            {
                var location = (IXmlLineInfo)element;
                entries.Add(
                    new(
                        relative,
                        location.LineNumber,
                        location.LinePosition,
                        "XamlElement",
                        element.Name.LocalName,
                        element.Value,
                        element.Value,
                        [],
                        element.Parent?.Name.LocalName ?? "",
                        false
                    )
                );
            }
        }
    }
}
if (args[0] == "check")
{
    var inventory =
        JsonSerializer.Deserialize<InventoryEntry[]>(
            File.ReadAllText(Path.Combine(root, "docs/localization/source-inventory.json"))
        ) ?? throw new InvalidDataException("Missing localization inventory.");
    var resources = XDocument
        .Load(Path.Combine(root, "src/Something.App/Strings/ja-JP/Resources.resw"))
        .Root!.Elements("data")
        .ToDictionary(
            element => (string)element.Attribute("name")!,
            element => (string?)element.Element("value") ?? "",
            StringComparer.OrdinalIgnoreCase
        );
    var expected = inventory
        .GroupBy(item => item.Id)
        .ToDictionary(group => group.Key, group => group.ToArray());
    var actual = entries
        .GroupBy(Identity)
        .ToDictionary(group => group.Key, group => group.ToArray());
    var errors = new List<string>();
    foreach (string id in expected.Keys.Union(actual.Keys))
    {
        expected.TryGetValue(id, out var recorded);
        actual.TryGetValue(id, out var found);
        if (recorded?.Length != found?.Length)
            errors.Add(
                $"Unreviewed/stale source: {found?.FirstOrDefault()?.File ?? recorded![0].File} ({id})."
            );
    }
    foreach (var item in inventory)
    {
        if ((item.Resource is null) == (item.Reason is null))
            errors.Add(
                $"Exactly one resource or exclusion reason is required: {item.File}:{item.Line}."
            );
        if (item.Resource is not { } key)
            continue;
        if (!resources.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"Missing/empty resource: {key}.");
            continue;
        }
        if (actual.TryGetValue(item.Id, out var sources) && sources[0].Kind == "CSharpFormat")
        {
            try
            {
                var format = CompositeFormat.Parse(value);
                int required = format.MinimumArgumentCount;
                if (required != sources[0].Arguments.Length)
                    errors.Add($"Format argument count mismatch: {key}.");
                // Let the runtime distinguish escaped braces from real format holes.
                // Do not allocate according to an untrusted translated argument index.
                if (required != sources[0].Arguments.Length)
                    continue;
                var probes = Enumerable.Range(0, required).Select(_ => new FormatProbe()).ToArray();
                _ = string.Format(
                    CultureInfo.InvariantCulture,
                    format,
                    probes.Cast<object?>().ToArray()
                );
                for (int index = 0; index < probes.Length; index++)
                    if (!probes[index].Used)
                        errors.Add($"Missing format argument {index}: {key}.");
            }
            catch (FormatException)
            {
                errors.Add($"Invalid composite format: {key}.");
            }
        }
    }
    foreach (
        string key in resources.Keys.Except(
            inventory.Where(item => item.Resource is not null).Select(item => item.Resource!),
            StringComparer.OrdinalIgnoreCase
        )
    )
        errors.Add($"Resource has no inventoried source: {key}.");
    foreach (string error in errors.Distinct())
        Console.Error.WriteLine(error);
    Console.WriteLine(
        $"Checked {entries.Count} source strings; {resources.Count} Japanese resources; {errors.Count} errors."
    );
    return errors.Count == 0 ? 0 : 1;
}
Directory.CreateDirectory(Path.Combine(root, "artifacts"));
File.WriteAllText(
    Path.Combine(root, "artifacts", "localization-extracted.json"),
    JsonSerializer.Serialize(
        entries
            .OrderBy(entry => entry.File, StringComparer.Ordinal)
            .ThenBy(entry => entry.Line)
            .ThenBy(entry => entry.Column),
        new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }
    )
);
Console.WriteLine($"Extracted {entries.Count} source strings.");
return 0;

static string Identity(SourceString source) => source.Id;

static bool IsTextProperty(string name) =>
    name
        is "Text"
            or "Content"
            or "Header"
            or "Title"
            or "PlaceholderText"
            or "PrimaryButtonText"
            or "SecondaryButtonText"
            or "CloseButtonText"
            or "AutomationProperties.Name"
            or "AutomationProperties.HelpText"
            or "ToolTipService.ToolTip"
            or "Description";

internal sealed record SourceString(
    string File,
    int Line,
    int Column,
    string Kind,
    string Member,
    string Text,
    string Source,
    string[] Arguments,
    string Context,
    bool Diagnostic
)
{
    public string Id =>
        Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    string.Join(
                        '\u001f',
                        File,
                        Kind,
                        Member,
                        Text,
                        string.Join('\u001e', Arguments)
                    )
                )
            )
        );
}

internal sealed record InventoryEntry(
    string Id,
    string File,
    int Line,
    int Column,
    string? Resource,
    string? Reason
);

internal sealed class FormatProbe : IFormattable
{
    public bool Used { get; private set; }

    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        Used = true;
        return "value";
    }
}
