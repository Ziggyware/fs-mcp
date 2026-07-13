using System.Text.Json;
using ModelContextProtocol.Server;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Scripting;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using SharpToken;
using Humanizer;

namespace RoslynMcp;

[McpServerToolType]
internal static class Tools
{
    static readonly JsonSerializerOptions J = new() { WriteIndented = false };
    static readonly GptEncoding Enc = GptEncoding.GetEncoding("cl100k_base");

    static SyntaxTree T(string s) => CSharpSyntaxTree.ParseText(s);
    static string Ser(object o) => JsonSerializer.Serialize(o, J);
    static int Tok(string s) => Enc.Encode(s).Count;

    [McpServerTool, System.ComponentModel.Description("Parse C# source, return diagnostics.")]
    public static string Parse(string source)
    {
        var d = T(source).GetDiagnostics().ToArray();
        return Ser(new
        {
            ok = !d.Any(x => x.Severity == DiagnosticSeverity.Error),
            tokens = Tok(source),
            diagnostics = d.Select(x => new
            {
                sev = x.Severity.ToString(), msg = x.GetMessage(),
                start = x.Location.SourceSpan.Start, len = x.Location.SourceSpan.Length,
                line = x.Location.GetLineSpan().StartLinePosition.Line + 1
            })
        });
    }

    [McpServerTool, System.ComponentModel.Description("Find nodes by SyntaxKind name.")]
    public static string FindNodes(string source, string kind)
    {
        if (!Enum.TryParse<SyntaxKind>(kind, out var k))
            throw new ArgumentException($"unrecognized SyntaxKind: {kind}");
        var m = T(source).GetRoot().DescendantNodes().Where(n => n.IsKind(k))
            .Select(n => new { kind = n.Kind().ToString(), start = n.Span.Start, len = n.Span.Length,
                text = n.ToString() is { Length: > 200 } s ? s[..200] + "…" : n.ToString() })
            .ToArray();
        return Ser(new { count = m.Length, matches = m });
    }

    [McpServerTool, System.ComponentModel.Description("Splice [start,start+len) with replacement, diffed.")]
    public static string ReplaceNode(string source, int start, int len, string replacement)
    {
        if (start < 0 || len < 0 || start + len > source.Length)
            throw new ArgumentException("span out of bounds");
        var nt = source[..start] + replacement + source[(start + len)..];
        var eb = T(source).GetDiagnostics().Count(x => x.Severity == DiagnosticSeverity.Error);
        var ea = T(nt).GetDiagnostics().Count(x => x.Severity == DiagnosticSeverity.Error);
        var diff = InlineDiffBuilder.Diff(source, nt);
        return Ser(new
        {
            text = nt, newErrors = ea > eb, errBefore = eb, errAfter = ea,
            diff = diff.Lines.Where(l => l.Type != ChangeType.Unchanged)
                .Select(l => new { type = l.Type.ToString(), text = l.Text })
        });
    }

    [McpServerTool, System.ComponentModel.Description("Format via Roslyn defaults.")]
    public static string Format(string source)
    {
        using var w = new Microsoft.CodeAnalysis.AdhocWorkspace();
        return Formatter.Format(T(source).GetRoot(), w).ToFullString();
    }

    [McpServerTool, System.ComponentModel.Description("Compile+run C# snippet. Unsandboxed.")]
    public static async Task<string> Execute(string code)
    {
        try
        {
            var r = await CSharpScript.RunAsync(code, ScriptOptions.Default);
            return Ser(new { ok = true, result = r.ReturnValue?.ToString() });
        }
        catch (CompilationErrorException e) { return Ser(new { ok = false, errors = e.Diagnostics.Select(d => d.ToString()) }); }
        catch (Exception e) { return Ser(new { ok = false, error = e.Message }); }
    }

    [McpServerTool, System.ComponentModel.Description("Instantiate a Roslyn analyzer id against minimal single-file Compilation.")]
    public static async Task<string> Analyze(string source)
    {
        var tree = T(source);
        var comp = CSharpCompilation.Create("_", new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
        var diags = comp.GetDiagnostics();
        return Ser(new { count = diags.Length, summary = "diagnostic".ToQuantity(diags.Length),
            diagnostics = diags.Select(d => new { id = d.Id, sev = d.Severity.ToString(), msg = d.GetMessage() }) });
    }

    [McpServerTool, System.ComponentModel.Description("Token count, cl100k_base approximation.")]
    public static string CountTokens(string text) => Ser(new { tokens = Tok(text), chars = text.Length });

    [McpServerTool, System.ComponentModel.Description(
        "Define a reusable workflow: ordered tool-call steps with {{param}} substitution. " +
        "stepsJson: JSON array of {tool, args}. args keys MUST exactly match the target " +
        "tool method's C# parameter names (source, start, len, replacement, kind, code, text — " +
        "case-sensitive). paramsJson: JSON array of param names usable as {{name}} in any step's args. " +
        "If any step targets Execute, returns a pending confirmationToken instead of activating — " +
        "call ConfirmWorkflow(token) to activate.")]
    public static string DefineWorkflow(string name, string stepsJson, string paramsJson)
    {
        var steps = JsonSerializer.Deserialize<WorkflowStep[]>(stepsJson)
            ?? throw new ArgumentException("stepsJson did not parse to a step array");
        var pars = JsonSerializer.Deserialize<string[]>(paramsJson) ?? Array.Empty<string>();
        var outcome = WorkflowStore.Stage(name, pars, steps);
        return outcome == "active"
            ? Ser(new { status = "active", name })
            : Ser(new { status = "pending_confirmation", name, confirmationToken = outcome,
                note = "workflow contains Execute; call ConfirmWorkflow to activate" });
    }

    [McpServerTool, System.ComponentModel.Description("Activate a pending workflow (one containing Execute) by its confirmation token.")]
    public static string ConfirmWorkflow(string token)
    {
        var w = WorkflowStore.Confirm(token);
        return Ser(new { status = "active", name = w.Name });
    }

    [McpServerTool, System.ComponentModel.Description("List all active (invokable) workflows with their required params and step tools.")]
    public static string ListWorkflows() => Ser(new
    {
        workflows = WorkflowStore.ListActive().Select(w => new
        {
            w.Name, w.Params, steps = w.Steps.Select(s => s.Tool), containsExecute = w.HasExecute
        })
    });

    [McpServerTool, System.ComponentModel.Description(
        "Run an active workflow. paramValuesJson: JSON object mapping param names to values. " +
        "Executes steps in order, halts and reports on first step failure.")]
    public static string RunWorkflow(string name, string paramValuesJson)
    {
        var w = WorkflowStore.Get(name);
        var vals = JsonSerializer.Deserialize<Dictionary<string, string>>(paramValuesJson)
            ?? throw new ArgumentException("paramValuesJson did not parse to an object");
        return WorkflowEngine.Run(w, vals);
    }
}
