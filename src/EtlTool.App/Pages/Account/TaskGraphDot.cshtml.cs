using System.Text;
using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using EtlTool.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace EtlTool.App.Pages.Account;

/// <summary>
/// Export the task dependency graph as Graphviz DOT format.
/// Admin can paste into https://dreampuf.github.io/GraphvizOnline/ or VS Code's
/// "Graphviz Preview" extension to visualize.
/// </summary>
[Authorize(Roles = "Admin,Operator")]
public class TaskGraphDotModel : PageModel
{
    private readonly AppDbContext _db;

    public TaskGraphDotModel(AppDbContext db) { _db = db; }

    public async Task<IActionResult> OnGetAsync(string? format, CancellationToken ct)
    {
        var tasks = await _db.EtlTasks
            .AsNoTracking()
            .Select(t => new { t.Id, t.Name, t.Enabled, t.DependsOnTaskIds })
            .ToListAsync(ct);

        var fmt = (format ?? "dot").Trim().ToLowerInvariant();
        return fmt switch
        {
            "mermaid" => RenderMermaid(tasks),
            _ => RenderDot(tasks),
        };
    }

    private IActionResult RenderDot(IEnumerable<dynamic> tasks)
    {
        var taskList = tasks.ToList();
        var sb = new StringBuilder();
        sb.AppendLine("digraph EtlTaskGraph {");
        sb.AppendLine("  rankdir=LR;");
        sb.AppendLine("  node [shape=box, style=\"rounded,filled\", fontname=\"Helvetica\"];");
        sb.AppendLine();

        var idSet = new HashSet<Guid>();
        foreach (var t in taskList) idSet.Add((Guid)t.Id);

        foreach (var t in taskList)
        {
            var enabled = (bool)t.Enabled;
            var color = enabled ? "#dcfce7" : "#f3f4f6";
            var label = EscapeDot((string)t.Name);
            sb.AppendLine($"  \"{t.Id}\" [label=\"{label}\", fillcolor=\"{color}\"];");
        }
        sb.AppendLine();

        foreach (var t in taskList)
        {
            var parents = TaskDependencyChecker.ParseDependsOnIds((string?)t.DependsOnTaskIds);
            foreach (var p in parents)
            {
                if (!idSet.Contains(p)) continue;
                sb.AppendLine($"  \"{p}\" -> \"{t.Id}\";");
            }
        }
        sb.AppendLine("}");

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return File(bytes, "text/vnd.graphviz; charset=utf-8",
            $"etltool-tasks-{DateTime.UtcNow:yyyyMMdd}.dot");
    }

    private IActionResult RenderMermaid(IEnumerable<dynamic> tasks)
    {
        // Mermaid flowchart LR — paste into any markdown viewer that supports
        // Mermaid (GitHub, GitLab, Obsidian, VS Code preview).
        var taskList = tasks.ToList();
        var sb = new StringBuilder();
        sb.AppendLine("flowchart LR");

        var idSet = new HashSet<Guid>();
        foreach (var t in taskList) idSet.Add((Guid)t.Id);

        // Mermaid node ids must be alpha-numeric (no hyphens / dots) — strip GUID dashes
        string Slug(Guid id) => "T" + id.ToString("N");

        foreach (var t in taskList)
        {
            var enabled = (bool)t.Enabled;
            var label = EscapeMermaid((string)t.Name);
            var nodeId = Slug((Guid)t.Id);
            sb.AppendLine($"    {nodeId}[\"{label}\"]");
            // Per-node class for color
            sb.AppendLine(enabled
                ? $"    class {nodeId} enabled"
                : $"    class {nodeId} disabled");
        }
        sb.AppendLine();

        foreach (var t in taskList)
        {
            var parents = TaskDependencyChecker.ParseDependsOnIds((string?)t.DependsOnTaskIds);
            foreach (var p in parents)
            {
                if (!idSet.Contains(p)) continue;
                sb.AppendLine($"    {Slug(p)} --> {Slug((Guid)t.Id)}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("    classDef enabled fill:#dcfce7,stroke:#15803d,stroke-width:1px");
        sb.AppendLine("    classDef disabled fill:#f3f4f6,stroke:#6b7280,stroke-width:1px");

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return File(bytes, "text/plain; charset=utf-8",
            $"etltool-tasks-{DateTime.UtcNow:yyyyMMdd}.mmd");
    }

    private static string EscapeDot(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string EscapeMermaid(string s)
        => s.Replace("\"", "&quot;").Replace("[", "&#91;").Replace("]", "&#93;");
}
