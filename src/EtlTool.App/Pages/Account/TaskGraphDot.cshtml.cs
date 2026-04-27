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

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var tasks = await _db.EtlTasks
            .AsNoTracking()
            .Select(t => new { t.Id, t.Name, t.Enabled, t.DependsOnTaskIds })
            .ToListAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine("digraph EtlTaskGraph {");
        sb.AppendLine("  rankdir=LR;");
        sb.AppendLine("  node [shape=box, style=\"rounded,filled\", fontname=\"Helvetica\"];");
        sb.AppendLine();

        // Nodes
        var idToLabel = tasks.ToDictionary(t => t.Id, t => t.Name);
        foreach (var t in tasks)
        {
            var color = t.Enabled ? "#dcfce7" : "#f3f4f6"; // green if enabled, gray if not
            var label = EscapeDot(t.Name);
            sb.AppendLine($"  \"{t.Id}\" [label=\"{label}\", fillcolor=\"{color}\"];");
        }
        sb.AppendLine();

        // Edges (parent → child)
        foreach (var t in tasks)
        {
            var parents = TaskDependencyChecker.ParseDependsOnIds(t.DependsOnTaskIds);
            foreach (var p in parents)
            {
                if (!idToLabel.ContainsKey(p)) continue;
                sb.AppendLine($"  \"{p}\" -> \"{t.Id}\";");
            }
        }

        sb.AppendLine("}");

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return File(bytes, "text/vnd.graphviz; charset=utf-8",
            $"etltool-tasks-{DateTime.UtcNow:yyyyMMdd}.dot");
    }

    private static string EscapeDot(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
