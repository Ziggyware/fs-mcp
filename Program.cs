using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Length < 1) { Console.Error.WriteLine("usage: code-mcp <root-directory>"); return 1; }
var root = Path.GetFullPath(args[0]);
if (!Directory.Exists(root)) { Console.Error.WriteLine($"root does not exist: {root}"); return 1; }
CodeMcp.FsTools.Root = root;

var b = Host.CreateApplicationBuilder(args);
b.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
b.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();
await b.Build().RunAsync();
return 0;
