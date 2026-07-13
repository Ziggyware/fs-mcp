using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using SharpToken;

namespace CodeMcp;

[McpServerToolType]
internal static class CodeTools
{
    static string Ser(object o) => FsTools.Ser(o);
    static readonly GptEncoding Enc = GptEncoding.GetEncoding("cl100k_base");
    record EditSpan(int start, int len, string replacement);
    static readonly JsonSerializerOptions JIn = new() { PropertyNameCaseInsensitive = true };

    // ── the canonical edit cycle: Outline/FindNodes → Edit (verdict, not payload) ──

    [McpServerTool, System.ComponentModel.Description(
        "Apply edits [{start,len,replacement}] (byte spans from Outline/FindNodes/FindReferences) " +
        "to a .cs file on disk, atomically; overlaps rejected. Returns the workspace-wide error " +
        "delta — introduced[], resolved[] — not the file. Introduced errors may be in OTHER files; " +
        "that is the point.")]
    public static string Edit(string relativePath, string editsJson) => FsTools.Guarded(() =>
    {
        var before = Workspace.Errors();
        var full = FsTools.Resolve(relativePath);
        if (!File.Exists(full)) throw new McpException($"not found: {relativePath}");
        var text = ApplyEdits(File.ReadAllText(full), editsJson, out var applied);
        FsTools.AtomicWrite(full, text);
        Workspace.Invalidate(relativePath, text);
        var after = Workspace.Errors();
        return Ser(new { edited = relativePath, editsApplied = applied,
                         ok = after.Length <= before.Length,
                         delta = Workspace.Delta(before, after) });
    });

    static string ApplyEdits(string source, string editsJson, out int applied)
    {
        var edits = JsonSerializer.Deserialize<EditSpan[]>(editsJson, JIn)
            ?? throw new McpException("editsJson did not parse");
        if (edits.Length == 0) throw new McpException("no edits given");
        var ordered = edits.OrderByDescending(e => e.start).ThenByDescending(e => e.len).ToArray();
        for (int i = 0; i < ordered.Length - 1; i++)
            if (ordered[i + 1].start + ordered[i + 1].len > ordered[i].start)
                throw new McpException($"overlapping edits at {ordered[i + 1].start} and {ordered[i].start}");
        var text = source;
        foreach (var e in ordered)
        {
            if (e.start < 0 || e.len < 0 || e.start + e.len > text.Length)
                throw new McpException($"edit span [{e.start},{e.start + e.len}) out of bounds");
            text = string.Concat(text.AsSpan(0, e.start), e.replacement, text.AsSpan(e.start + e.len));
        }
        applied = ordered.Length;
        return text;
    }

    [McpServerTool, System.ComponentModel.Description(
        "Workspace-wide compile check (incremental — cheap after the first call). Returns error " +
        "count and first 50 errors with file/line. Edit already returns the delta; use this for " +
        "the full current picture.")]
    public static string Check() => FsTools.Guarded(() =>
    {
        var errs = Workspace.Errors();
        return Ser(new { ok = errs.Length == 0, errorCount = errs.Length,
                         truncated = errs.Length > 50,
                         errors = errs.Take(50).Select(Workspace.Shape) });
    });

    [McpServerTool, System.ComponentModel.Description(
        "Drop the cached workspace and re-read all .cs from disk. Call after out-of-band changes " +
        "(git pull, external editor) — stale state yields wrong deltas.")]
    public static string Reload() => FsTools.Guarded(() =>
    {
        Workspace.Reset();
        return Ser(new { reloaded = true, files = Workspace.Comp().SyntaxTrees.Count() });
    });

    // ── navigation ──

    [McpServerTool, System.ComponentModel.Description(
        "Structural outline of a .cs file: namespaces, types, members with line numbers and byte " +
        "spans (spans feed Edit). No bodies. maxDepth hard cutoff.")]
    public static string Outline(string relativePath, int maxDepth = 4) => FsTools.Guarded(() =>
    {
        var root = Workspace.Tree(relativePath).GetRoot();
        object Walk(SyntaxNode n, int depth)
        {
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
            return new { kind = n.Kind().ToString(), name,
                         line = n.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                         start = n.Span.Start, len = n.Span.Length, children };
        }
        return Ser(Walk(root, 0));
    });

    [McpServerTool, System.ComponentModel.Description(
        "Find nodes by SyntaxKind name (case-insensitive) in a .cs file; byte spans feed Edit. " +
        "Node text truncated to 200 chars.")]
    public static string FindNodes(string relativePath, string kind) => FsTools.Guarded(() =>
    {
        if (!Enum.TryParse<SyntaxKind>(kind, ignoreCase: true, out var k))
            throw new McpException($"unrecognized SyntaxKind: {kind}");
        var m = Workspace.Tree(relativePath).GetRoot().DescendantNodes().Where(n => n.IsKind(k))
            .Select(n =>
            {
                var s = n.ToString();
                return new { start = n.Span.Start, len = n.Span.Length,
                             line = n.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                             text = s.Length > 200 ? s[..200] + "…" : s };
            })
            .ToArray();
        return Ser(new { count = m.Length, matches = m });
    });

    [McpServerTool, System.ComponentModel.Description(
        "Syntactic ancestry chain at a byte position in a .cs file: parameter → method → type → " +
        "namespace. No semantics — FindReferences for that.")]
    public static string Explain(string relativePath, int position) => FsTools.Guarded(() =>
    {
        var tree = Workspace.Tree(relativePath);
        var root = tree.GetRoot();
        if (position < 0 || position > root.FullSpan.End)
            throw new McpException("position out of bounds");
        var chain = new List<string>();
        for (var n = (SyntaxNode?)root.FindToken(position).Parent; n is not null; n = n.Parent)
        {
            var desc = n switch
            {
                BaseNamespaceDeclarationSyntax ns => $"namespace {ns.Name}",
                TypeDeclarationSyntax t => $"{t.Keyword.Text} {t.Identifier.Text}",
                EnumDeclarationSyntax e => $"enum {e.Identifier.Text}",
                MethodDeclarationSyntax m => $"method {m.Identifier.Text}",
                ParameterSyntax p => $"parameter {p.Identifier.Text}",
                PropertyDeclarationSyntax p => $"property {p.Identifier.Text}",
                _ => null
            };
            if (desc is not null) chain.Add(desc);
        }
        chain.Reverse();
        return Ser(new { position, chain });
    });

    // ── semantics: the questions strings can't answer ──

    [McpServerTool, System.ComponentModel.Description(
        "Find all references to the symbol at a byte position in a .cs file, workspace-wide " +
        "(capped 200). Run before any rename or signature change; spans feed Edit.")]
    public static string FindReferences(string relativePath, int position) => FsTools.Guarded(() =>
    {
        var comp = Workspace.Comp();
        var tree = Workspace.Tree(relativePath);
        var model = comp.GetSemanticModel(tree);
        var node = tree.GetRoot().FindToken(position).Parent
            ?? throw new McpException($"no node at {position}");
        var sym = model.GetSymbolInfo(node).Symbol ?? model.GetDeclaredSymbol(node)
            ?? throw new McpException($"no symbol at {position}; give an identifier position");
        var refs = comp.SyntaxTrees.SelectMany(t =>
        {
            var m = comp.GetSemanticModel(t);
            return t.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>()
                .Where(id => SymbolEqualityComparer.Default.Equals(m.GetSymbolInfo(id).Symbol, sym))
                .Select(id => new { file = t.FilePath,
                    line = id.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    start = id.SpanStart, len = id.Span.Length });
        }).Take(200).ToArray();
        return Ser(new { symbol = sym.ToDisplayString(), kind = sym.Kind.ToString(),
                         count = refs.Length, references = refs });
    });

    // ── create / format ──

    [McpServerTool, System.ComponentModel.Description(
        "Scaffold a new .cs file on disk (kind: class|interface|record|enum), public, file-scoped " +
        "namespace, given usings. Parseable by construction; fails if the file exists.")]
    public static string Scaffold(string relativePath, string kind, string typeName, string ns,
        string[] usings) => FsTools.Guarded(() =>
    {
        var full = FsTools.Resolve(relativePath);
        if (File.Exists(full)) throw new McpException($"'{relativePath}' already exists");
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
            _ => throw new McpException($"kind must be class|interface|record|enum, got '{kind}'")
        };
        var unit = SyntaxFactory.CompilationUnit()
            .AddUsings(usings.Select(u => SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(u))).ToArray())
            .AddMembers(SyntaxFactory.FileScopedNamespaceDeclaration(SyntaxFactory.ParseName(ns)).AddMembers(type))
            .NormalizeWhitespace();
        var text = unit.ToFullString();
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        Workspace.Invalidate(relativePath, text);
        return Ser(new { created = relativePath, bytes = text.Length });
    });

    [McpServerTool, System.ComponentModel.Description(
        "Format a .cs file in place with Roslyn defaults; atomic write. Returns changed:false if " +
        "already formatted.")]
    public static string Format(string relativePath) => FsTools.Guarded(() =>
    {
        var full = FsTools.Resolve(relativePath);
        if (!File.Exists(full)) throw new McpException($"not found: {relativePath}");
        var source = File.ReadAllText(full);
        using var w = new AdhocWorkspace();
        var formatted = Formatter.Format(
            CSharpSyntaxTree.ParseText(source).GetRoot(), w).ToFullString();
        if (formatted == source) return Ser(new { formatted = relativePath, changed = false });
        FsTools.AtomicWrite(full, formatted);
        Workspace.Invalidate(relativePath, formatted);
        return Ser(new { formatted = relativePath, changed = true });
    });

    [McpServerTool, System.ComponentModel.Description(
        "Token count of a file (cl100k_base approximation) — budget check before Read/ReadRange.")]
    public static string CountTokens(string relativePath) => FsTools.Guarded(() =>
    {
        var full = FsTools.Resolve(relativePath);
        if (!File.Exists(full)) throw new McpException($"not found: {relativePath}");
        var text = File.ReadAllText(full);
        return Ser(new { tokens = Enc.Encode(text).Count, chars = text.Length });
    });
}
