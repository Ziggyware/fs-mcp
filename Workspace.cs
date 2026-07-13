using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CodeMcp;

// Incremental compilation over all .cs under FsTools.Root. Edits invalidate one
// tree; Roslyn reuses the rest. Every CodeTools tool is a thin projection of this.
static class Workspace
{
    static readonly Lazy<MetadataReference[]> Refs = new(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToArray());

    static readonly Dictionary<string, SyntaxTree> Trees = new();   // rel path -> tree
    static CSharpCompilation? _comp;
    static readonly object Gate = new();

    public static CSharpCompilation Comp()
    {
        lock (Gate)
        {
            if (_comp is not null) return _comp;
            Trees.Clear();
            var trashSeg = $"{Path.DirectorySeparatorChar}.trash{Path.DirectorySeparatorChar}";
            foreach (var f in Directory.EnumerateFiles(FsTools.Root, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains(trashSeg)))
            {
                var rel = Path.GetRelativePath(FsTools.Root, f);
                Trees[rel] = CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: rel);
            }
            return _comp = CSharpCompilation.Create("ws", Trees.Values, Refs.Value,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        }
    }

    // One file changed: ReplaceSyntaxTree, not rebuild. newContent null = deleted.
    public static void Invalidate(string rel, string? newContent)
    {
        lock (Gate)
        {
            if (_comp is null) return;                       // not built yet; next Comp() reads disk
            var old = Trees.GetValueOrDefault(rel);
            if (newContent is null)
            {
                if (old is not null) { _comp = _comp.RemoveSyntaxTrees(old); Trees.Remove(rel); }
                return;
            }
            var nt = CSharpSyntaxTree.ParseText(newContent, path: rel);
            _comp = old is null ? _comp.AddSyntaxTrees(nt) : _comp.ReplaceSyntaxTree(old, nt);
            Trees[rel] = nt;
        }
    }

    // Re-read one path from disk (FS mutators call this; content unknown to them).
    public static void InvalidateFromDisk(string rel)
    {
        var full = Path.Combine(FsTools.Root, rel);
        Invalidate(rel, File.Exists(full) ? File.ReadAllText(full) : null);
    }

    // Directory-level or out-of-band change: drop everything, lazily rebuild.
    public static void Reset() { lock (Gate) { _comp = null; Trees.Clear(); } }

    public static SyntaxTree Tree(string rel) =>
        Comp().SyntaxTrees.FirstOrDefault(t => t.FilePath.Equals(rel,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        ?? throw new ModelContextProtocol.McpException($"no .cs file in workspace: {rel}");

    public static Diagnostic[] Errors() => Comp().GetDiagnostics()
        .Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();

    public static object Shape(Diagnostic d) => new
    {
        id = d.Id, msg = d.GetMessage(), file = d.Location.SourceTree?.FilePath,
        line = d.Location.SourceTree is null ? -1
             : d.Location.GetLineSpan().StartLinePosition.Line + 1
    };

    public static object Delta(Diagnostic[] before, Diagnostic[] after)
    {
        static string Key(Diagnostic d) =>
            $"{d.Id}|{d.Location.SourceTree?.FilePath}|{d.GetMessage()}";
        var b = before.Select(Key).ToHashSet();
        var a = after.Select(Key).ToHashSet();
        return new
        {
            errorCount = after.Length,
            introduced = after.Where(d => !b.Contains(Key(d))).Take(20).Select(Shape),
            resolved = before.Where(d => !a.Contains(Key(d))).Take(20).Select(Shape)
        };
    }
}
