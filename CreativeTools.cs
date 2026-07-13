using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;
using System.Text.Json;

namespace RoslynMcp;

[McpServerToolType]
internal static class CreativeTools
{
    static readonly JsonSerializerOptions J = new() { WriteIndented = false };
    static string Ser(object o) => JsonSerializer.Serialize(o, J);

    [McpServerTool, System.ComponentModel.Description(
        "Scaffold a syntactically valid starting file for a class/interface/record/enum via SyntaxFactory. " +
        "kind: class|interface|record|enum. Guarantees parseable output by construction.")]
    public static string ScaffoldFile(string kind, string typeName, string ns, string[] usings)
    {
        var members = SyntaxFactory.List<MemberDeclarationSyntax>();
        MemberDeclarationSyntax type = kind.ToLowerInvariant() switch
        {
            "class" => SyntaxFactory.ClassDeclaration(typeName)
                .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword)),
            "interface" => SyntaxFactory.InterfaceDeclaration(typeName)
                .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword)),
            "record" => SyntaxFactory.RecordDeclaration(SyntaxKind.RecordDeclaration,
                SyntaxFactory.Token(SyntaxKind.RecordKeyword), typeName)
                .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
                .WithOpenBraceToken(SyntaxFactory.Token(SyntaxKind.OpenBraceToken))
                .WithCloseBraceToken(SyntaxFactory.Token(SyntaxKind.CloseBraceToken)),
            "enum" => SyntaxFactory.EnumDeclaration(typeName)
                .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword)),
            _ => throw new ArgumentException($"unrecognized kind: {kind}")
        };

        var ns_ = SyntaxFactory.FileScopedNamespaceDeclaration(SyntaxFactory.ParseName(ns))
            .AddMembers(type);
        var unit = SyntaxFactory.CompilationUnit()
            .AddUsings(usings.Select(u => SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(u))).ToArray())
            .AddMembers(ns_)
            .NormalizeWhitespace();

        return unit.ToFullString();
    }

    [McpServerTool, System.ComponentModel.Description(
        "Apply N edits [{start,len,replacement}] to source in one call, back-to-front by offset so " +
        "edits don't invalidate each other. Overlapping ranges are rejected, not silently applied.")]
    public static string MultiReplace(string source, string editsJson)
    {
        var edits = JsonSerializer.Deserialize<(int start, int len, string replacement)[]>(editsJson)
            ?? throw new ArgumentException("editsJson did not parse");
        var ordered = edits.OrderByDescending(e => e.start).ToArray();
        for (int i = 0; i < ordered.Length - 1; i++)
        {
            var (curStart, curLen, _) = ordered[i];
            var (prevStart, prevLen, _) = ordered[i + 1];
            if (prevStart + prevLen > curStart)
                throw new ArgumentException($"overlapping edits at {prevStart} and {curStart}");
        }
        var text = source;
        foreach (var (start, len, replacement) in ordered)
        {
            if (start < 0 || len < 0 || start + len > text.Length)
                throw new ArgumentException($"edit span [{start},{start + len}) out of bounds");
            text = text[..start] + replacement + text[(start + len)..];
        }
        return Ser(new { text, editsApplied = ordered.Length });
    }

    [McpServerTool, System.ComponentModel.Description(
        "Depth-bounded structural outline of a file: namespaces, types, members with line numbers, no bodies. " +
        "maxDepth default 4. Hard cutoff, not adaptive.")]
    public static string TreeSummary(string source, int maxDepth = 4)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var lines = source.Split('\n');
        object Walk(SyntaxNode n, int depth)
        {
            var line = source[..Math.Min(n.SpanStart, source.Length)].Count(c => c == '\n') + 1;
            var name = n switch
            {
                BaseNamespaceDeclarationSyntax ns => ns.Name.ToString(),
                BaseTypeDeclarationSyntax t => t.Identifier.Text,
                MethodDeclarationSyntax m => m.Identifier.Text,
                PropertyDeclarationSyntax p => p.Identifier.Text,
                FieldDeclarationSyntax f => string.Join(",", f.Declaration.Variables.Select(v => v.Identifier.Text)),
                _ => n.Kind().ToString()
            };
            var children = depth >= maxDepth ? Array.Empty<object>()
                : n.ChildNodes()
                    .Where(c => c is BaseNamespaceDeclarationSyntax or BaseTypeDeclarationSyntax
                        or MethodDeclarationSyntax or PropertyDeclarationSyntax or FieldDeclarationSyntax)
                    .Select(c => Walk(c, depth + 1)).ToArray();
            return new { kind = n.Kind().ToString(), name, line, children };
        }
        return Ser(Walk(root, 0));
    }

    [McpServerTool, System.ComponentModel.Description(
        "Enclosing syntactic ancestry chain for an offset: e.g. parameter x of method Foo of class Bar " +
        "of namespace Baz. Purely syntactic, no semantic meaning.")]
    public static string Explain(string source, int position)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        if (position < 0 || position > source.Length)
            throw new ArgumentException("position out of bounds");
        var node = root.FindNode(new TextSpan(position, 0));
        var chain = new List<string>();
        for (var n = node; n is not null; n = n.Parent)
        {
            var desc = n switch
            {
                BaseNamespaceDeclarationSyntax ns => $"namespace {ns.Name}",
                TypeDeclarationSyntax t => $"{t.Keyword.Text} {t.Identifier.Text}",
                EnumDeclarationSyntax e => $"{e.EnumKeyword.Text} {e.Identifier.Text}",
                BaseTypeDeclarationSyntax bt => $"{bt.Kind().ToString().Replace("Declaration", "").ToLowerInvariant()} {bt.Identifier.Text}",
                MethodDeclarationSyntax m => $"method {m.Identifier.Text}",
                ParameterSyntax p => $"parameter {p.Identifier.Text}",
                PropertyDeclarationSyntax p => $"property {p.Identifier.Text}",
                _ => null
            };
            if (desc is not null) chain.Add(desc);
        }
        chain.Reverse();
        return Ser(new { position, chain });
    }
}
