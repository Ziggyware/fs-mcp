using System.Text.RegularExpressions;
using System.Xml.Linq;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CodeMcp;

[McpServerToolType]
internal static class ProjectTools
{
    static string Ser(object o) => FsTools.Ser(o);

    static XDocument ParseXml(string xml, string kind)
    {
        try { return XDocument.Parse(xml); }
        catch (System.Xml.XmlException e) { throw new McpException($"invalid {kind} XML: {e.Message}"); }
    }

    [McpServerTool, System.ComponentModel.Description(
        "Extract metadata from a .csproj under root: Sdk, TargetFramework(s), OutputType, common " +
        "build properties, Package/ProjectReferences, Compile includes/removes. Extraction only — " +
        "no resolution, no existence checks, no Directory.Build.props inheritance.")]
    public static string ReadCsproj(string relativePath) => FsTools.Guarded(() =>
    {
        var full = FsTools.Resolve(relativePath);
        if (!File.Exists(full)) throw new McpException($"not found: {relativePath}");
        var doc = ParseXml(File.ReadAllText(full), ".csproj");
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
        string? Prop(string el) => doc.Descendants(ns + el).Select(e => e.Value).FirstOrDefault();
        string[] Items(string el, string attr) => doc.Descendants(ns + el)
            .Select(e => e.Attribute(attr)?.Value).Where(v => !string.IsNullOrEmpty(v)).ToArray()!;
        return Ser(new
        {
            sdk = doc.Root?.Attribute("Sdk")?.Value,
            targetFrameworks = (Prop("TargetFrameworks")?.Split(';') ?? new[] { Prop("TargetFramework") })
                .Where(t => !string.IsNullOrEmpty(t)).ToArray(),
            outputType = Prop("OutputType") ?? "Library",
            properties = new
            {
                langVersion = Prop("LangVersion"), nullable = Prop("Nullable"),
                implicitUsings = Prop("ImplicitUsings"), rootNamespace = Prop("RootNamespace"),
                assemblyName = Prop("AssemblyName")
            },
            packageReferences = doc.Descendants(ns + "PackageReference")
                .Select(e => new { id = e.Attribute("Include")?.Value,
                                   version = e.Attribute("Version")?.Value
                                          ?? e.Element(ns + "Version")?.Value })
                .Where(p => p.id is not null).ToArray(),
            projectReferences = Items("ProjectReference", "Include"),
            compileIncludes = Items("Compile", "Include"),
            compileRemoves = Items("Compile", "Remove")
        });
    });

    [McpServerTool, System.ComponentModel.Description(
        "Extract declared projects from a solution file under root — classic .sln text or XML " +
        ".slnx, auto-detected. Paths as written; no existence checks.")]
    public static string ReadSolution(string relativePath) => FsTools.Guarded(() =>
    {
        var full = FsTools.Resolve(relativePath);
        if (!File.Exists(full)) throw new McpException($"not found: {relativePath}");
        var content = File.ReadAllText(full);
        if (content.TrimStart().StartsWith('<'))
        {
            var doc = ParseXml(content, ".slnx");
            var projects = doc.Descendants("Project")
                .Select(e => new { path = e.Attribute("Path")?.Value, name = (string?)null })
                .Where(p => p.path is not null).ToArray();
            return Ser(new { format = "slnx", projectCount = projects.Length, projects });
        }
        var classic = Regex.Matches(content,
                @"^Project\(""\{[^}]+\}""\)\s*=\s*""([^""]+)"",\s*""([^""]+)""", RegexOptions.Multiline)
            .Select(m => new { path = m.Groups[2].Value, name = (string?)m.Groups[1].Value })
            .Where(p => p.path.EndsWith("proj", StringComparison.OrdinalIgnoreCase))  // skip solution folders
            .ToArray();
        if (classic.Length == 0 && !content.Contains("Microsoft Visual Studio Solution File"))
            throw new McpException("content is neither .slnx XML nor classic .sln text");
        return Ser(new { format = "sln", projectCount = classic.Length, projects = classic });
    });
}
