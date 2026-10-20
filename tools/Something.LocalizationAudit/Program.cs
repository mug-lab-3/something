using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Verifies that display text lives in Resources.resw and that every reference resolves:
// - no Japanese text in C# string literals or XAML text, except synthetic smoke-test samples;
// - no literal words in XAML text properties (symbols such as "—" are allowed);
// - x:Uid, Strings.Get and Strings.Format keys exist, and every resource is referenced;
// - composite formats match the number of Strings.Format arguments;
// - every translation has the same keys and format arguments as Japanese and no Japanese text.
if (args.Length != 2 || args[0] != "check")
    throw new ArgumentException("Usage: Something.LocalizationAudit check <repository-root>");
string root = Path.GetFullPath(args[1]);
string stringsPath = Path.Combine(root, "src/Something.App/Strings");
string resourcePath = Path.Combine(stringsPath, "ja-JP/Resources.resw");

// Language names are shown in their own language in every translation.
string[] nativeNames = ["Settings_LanguageJapanese.Content"];
var errors = new List<string>();
var uids = new HashSet<string>(StringComparer.Ordinal);
var codeKeys = new Dictionary<string, List<int?>>(StringComparer.Ordinal);

foreach (
    string file in Directory
        .EnumerateFiles(Path.Combine(root, "src"), "*", SearchOption.AllDirectories)
        .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
        .Order(StringComparer.Ordinal)
)
{
    string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
    // Smoke tests may render synthetic sample text that is not part of the product UI.
    bool samples = relative.EndsWith(".Smoke.cs", StringComparison.Ordinal);
    if (Path.GetExtension(file) == ".cs")
        AuditCode(relative, File.ReadAllText(file), samples);
    else if (Path.GetExtension(file) == ".xaml")
        AuditXaml(relative, XDocument.Load(file, LoadOptions.SetLineInfo));
}

var resources = LoadResources(resourcePath, "ja-JP");
foreach (string uid in uids)
    if (!resources.Keys.Any(key => key.StartsWith(uid + ".", StringComparison.Ordinal)))
        errors.Add($"x:Uid has no resources: {uid}.");
foreach (string key in resources.Keys)
{
    int dot = key.IndexOf('.', StringComparison.Ordinal);
    bool used = dot > 0 ? uids.Contains(key[..dot]) : codeKeys.ContainsKey(key);
    if (!used)
        errors.Add($"Unreferenced resource: {key}.");
}
foreach (var (key, calls) in codeKeys)
{
    if (!resources.TryGetValue(key, out string? value))
    {
        errors.Add($"Missing resource: {key}.");
        continue;
    }
    CompositeFormat format;
    try
    {
        format = CompositeFormat.Parse(value);
    }
    catch (FormatException)
    {
        // Strings.Get returns the value unformatted, so only formatted keys must parse.
        if (calls.Any(count => count is not null))
            errors.Add($"Invalid composite format: {key}.");
        continue;
    }
    foreach (int? count in calls.Distinct())
    {
        int required = format.MinimumArgumentCount;
        if (count is null)
        {
            if (required != 0)
                errors.Add($"Strings.Get reads a composite format: {key}.");
            continue;
        }
        if (required != count)
        {
            errors.Add($"Format argument count mismatch ({count} given): {key}.");
            continue;
        }
        // Let the runtime distinguish escaped braces from real format holes.
        var probes = Enumerable.Range(0, count.Value).Select(_ => new FormatProbe()).ToArray();
        _ = string.Format(CultureInfo.InvariantCulture, format, probes.Cast<object?>().ToArray());
        for (int index = 0; index < probes.Length; index++)
            if (!probes[index].Used)
                errors.Add($"Missing format argument {index}: {key}.");
    }
}

int translations = 0;
foreach (
    string path in Directory
        .EnumerateFiles(stringsPath, "Resources.resw", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
)
{
    string language = Path.GetFileName(Path.GetDirectoryName(path)!);
    if (language == "ja-JP")
        continue;
    translations++;
    var translated = LoadResources(path, language);
    foreach (string key in resources.Keys.Except(translated.Keys, StringComparer.OrdinalIgnoreCase))
        errors.Add($"Missing {language} resource: {key}.");
    foreach (string key in translated.Keys.Except(resources.Keys, StringComparer.OrdinalIgnoreCase))
        errors.Add($"Extra {language} resource: {key}.");
    foreach (var (key, value) in translated)
    {
        if (HasJapanese(value) && !nativeNames.Contains(key, StringComparer.Ordinal))
            errors.Add($"Japanese text in {language} resource: {key}.");
        if (resources.TryGetValue(key, out string? source) && Holes(source) != Holes(value))
            errors.Add($"Format arguments differ from Japanese in {language}: {key}.");
    }
}

foreach (string error in errors.Distinct())
    Console.Error.WriteLine(error);
Console.WriteLine(
    $"Checked {resources.Count} resources in {translations + 1} languages, {uids.Count} x:Uid values and {codeKeys.Count} code keys; {errors.Count} errors."
);
return errors.Count == 0 ? 0 : 1;

void AuditCode(string relative, string source, bool samples)
{
    var syntax = CSharpSyntaxTree
        .ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14))
        .GetRoot();
    foreach (var node in syntax.DescendantNodes())
    {
        if (
            !samples
            && node
                is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression }
                    or InterpolatedStringTextSyntax
            && HasJapanese(
                node is LiteralExpressionSyntax literal ? literal.Token.ValueText : node.ToString()
            )
        )
            errors.Add($"Japanese text in code; use Resources.resw: {Location(relative, node)}.");
        if (
            node is InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax
                {
                    Expression: IdentifierNameSyntax { Identifier.ValueText: "Strings" },
                    Name.Identifier.ValueText: "Get" or "Format",
                } access,
            } call
        )
        {
            if (
                call.ArgumentList.Arguments.FirstOrDefault()?.Expression
                is not LiteralExpressionSyntax
                {
                    RawKind: (int)SyntaxKind.StringLiteralExpression
                } key
            )
            {
                errors.Add($"Resource key must be a string literal: {Location(relative, call)}.");
                continue;
            }
            int? count =
                access.Name.Identifier.ValueText == "Format"
                    ? call.ArgumentList.Arguments.Count - 1
                    : null;
            if (!codeKeys.TryGetValue(key.Token.ValueText, out var calls))
                codeKeys[key.Token.ValueText] = calls = [];
            calls.Add(count);
        }
    }
}

void AuditXaml(string relative, XDocument document)
{
    var xaml = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
    foreach (var element in document.Descendants())
    {
        if ((string?)element.Attribute(xaml + "Uid") is { } uid)
            uids.Add(uid);
        foreach (var attribute in element.Attributes())
            if (
                IsTextProperty(attribute.Name.LocalName)
                && !attribute.Value.StartsWith('{')
                && attribute.Value.Any(char.IsLetter)
                // The product name is not translated.
                && attribute.Value != "Something"
            )
                errors.Add(
                    $"Literal XAML text; use x:Uid: {relative}:{((IXmlLineInfo)attribute).LineNumber} {attribute.Name.LocalName}."
                );
        if (
            !element.HasElements
            && element.Nodes().OfType<XText>().Any(text => HasJapanese(text.Value))
        )
            errors.Add(
                $"Literal XAML text; use x:Uid: {relative}:{((IXmlLineInfo)element).LineNumber} {element.Name.LocalName}."
            );
    }
}

Dictionary<string, string> LoadResources(string path, string language)
{
    var loaded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var data in XDocument.Load(path).Root!.Elements("data"))
    {
        string key = (string)data.Attribute("name")!;
        string value = (string?)data.Element("value") ?? "";
        if (!loaded.TryAdd(key, value))
            errors.Add($"Duplicate {language} resource: {key}.");
        if (string.IsNullOrWhiteSpace(value))
            errors.Add($"Empty {language} resource: {key}.");
    }
    return loaded;
}

// The format holes a value uses, or -1 when it is not a composite format (plain text).
static int Holes(string value)
{
    try
    {
        return CompositeFormat.Parse(value).MinimumArgumentCount;
    }
    catch (FormatException)
    {
        return -1;
    }
}

static string Location(string relative, SyntaxNode node) =>
    $"{relative}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}";

static bool HasJapanese(string text) =>
    text.Any(c => c is >= '぀' and <= 'ヿ' or >= '一' and <= '鿿' or >= '！' and <= '～');

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

internal sealed class FormatProbe : IFormattable
{
    public bool Used { get; private set; }

    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        Used = true;
        return "value";
    }
}
