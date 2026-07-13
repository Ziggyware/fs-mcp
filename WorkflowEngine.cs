using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RoslynMcp;

static class WorkflowEngine
{
    public static object? RunStep(WorkflowStep step, Dictionary<string, string> bindings)
    {
        var m = typeof(Tools).GetMethod(step.Tool, BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException($"no tool method '{step.Tool}'");
        var ps = m.GetParameters();
        var args = new object[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            if (!step.Args.TryGetValue(ps[i].Name!, out var raw))
                throw new ArgumentException($"step '{step.Tool}' missing arg '{ps[i].Name}'");
            var sub = Regex.Replace(raw, @"\{\{(\w+)\}\}",
                mm => bindings.TryGetValue(mm.Groups[1].Value, out var v) ? v
                    : throw new ArgumentException($"unbound param '{mm.Groups[1].Value}'"));
            args[i] = Convert.ChangeType(sub, ps[i].ParameterType);
        }
        var result = m.Invoke(null, args);
        return result is Task t ? UnwrapTask(t) : result;
    }

    static object? UnwrapTask(Task t)
    {
        t.GetAwaiter().GetResult();
        var resultProp = t.GetType().GetProperty("Result");
        return resultProp?.GetValue(t);
    }

    public static string Run(Workflow w, Dictionary<string, string> paramValues)
    {
        foreach (var p in w.Params)
            if (!paramValues.ContainsKey(p))
                throw new ArgumentException($"workflow '{w.Name}' missing param '{p}'");

        var log = new List<object>();
        foreach (var step in w.Steps)
        {
            try
            {
                var r = RunStep(step, paramValues);
                log.Add(new { step = step.Tool, ok = true, result = r });
            }
            catch (Exception ex)
            {
                log.Add(new { step = step.Tool, ok = false, error = ex.Message });
                break;
            }
        }
        return JsonSerializer.Serialize(new { workflow = w.Name, log }, new JsonSerializerOptions { WriteIndented = false });
    }
}
