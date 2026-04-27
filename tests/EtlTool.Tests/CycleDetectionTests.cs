using EtlTool.Core.Engine;

namespace EtlTool.Tests;

public class CycleDetectionTests
{
    [Fact]
    public void Empty_dependencies_no_cycle()
    {
        var t = Guid.NewGuid();
        var result = TaskDependencyChecker.DetectCycle(
            t, Array.Empty<Guid>(), new Dictionary<Guid, IReadOnlyList<Guid>>());
        Assert.False(result.HasCycle);
        Assert.Null(result.CyclePath);
    }

    [Fact]
    public void Self_dependency_detected()
    {
        var t = Guid.NewGuid();
        var result = TaskDependencyChecker.DetectCycle(
            t, new[] { t }, new Dictionary<Guid, IReadOnlyList<Guid>>());
        Assert.True(result.HasCycle);
        Assert.Contains("自己", result.Reason!);
    }

    [Fact]
    public void Direct_two_node_cycle_detected()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        // Existing: B depends on A. Now we propose A depends on B → cycle A↔B.
        var allDeps = new Dictionary<Guid, IReadOnlyList<Guid>>
        {
            [b] = new[] { a },
        };
        var result = TaskDependencyChecker.DetectCycle(a, new[] { b }, allDeps);
        Assert.True(result.HasCycle);
        Assert.NotNull(result.CyclePath);
        Assert.Contains(a, result.CyclePath!);
        Assert.Contains(b, result.CyclePath);
    }

    [Fact]
    public void Three_node_cycle_detected()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        // Existing: B depends on A; C depends on B. Now A depends on C → A→C→B→A
        var allDeps = new Dictionary<Guid, IReadOnlyList<Guid>>
        {
            [b] = new[] { a },
            [c] = new[] { b },
        };
        var result = TaskDependencyChecker.DetectCycle(a, new[] { c }, allDeps);
        Assert.True(result.HasCycle);
    }

    [Fact]
    public void No_cycle_when_dependencies_form_a_chain()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        // Existing: B depends on A; C wants to depend on B → just a longer chain A→B→C, no cycle
        var allDeps = new Dictionary<Guid, IReadOnlyList<Guid>>
        {
            [b] = new[] { a },
        };
        var result = TaskDependencyChecker.DetectCycle(c, new[] { b }, allDeps);
        Assert.False(result.HasCycle);
    }

    [Fact]
    public void Diamond_dependencies_no_cycle()
    {
        var root = Guid.NewGuid();
        var l = Guid.NewGuid();
        var r = Guid.NewGuid();
        var bottom = Guid.NewGuid();
        // root has no deps; L depends on root; R depends on root; bottom depends on L AND R
        var allDeps = new Dictionary<Guid, IReadOnlyList<Guid>>
        {
            [l] = new[] { root },
            [r] = new[] { root },
        };
        // Adding bottom→L should not be a cycle even though we'll later add bottom→R too
        var result = TaskDependencyChecker.DetectCycle(bottom, new[] { l, r }, allDeps);
        Assert.False(result.HasCycle);
    }

    [Fact]
    public void Multiple_proposed_parents_first_cycle_wins()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        // B depends on A. We propose A depends on [c, b] — c is fine, b creates cycle.
        var allDeps = new Dictionary<Guid, IReadOnlyList<Guid>>
        {
            [b] = new[] { a },
        };
        var result = TaskDependencyChecker.DetectCycle(a, new[] { c, b }, allDeps);
        Assert.True(result.HasCycle);
    }

    [Fact]
    public void Cycle_through_existing_indirection_detected()
    {
        var x = Guid.NewGuid();
        var y = Guid.NewGuid();
        var z = Guid.NewGuid();
        // Existing graph: Y depends on X; X depends on Z. Adding Z → Y would close X→Z→Y→X.
        var allDeps = new Dictionary<Guid, IReadOnlyList<Guid>>
        {
            [y] = new[] { x },
            [x] = new[] { z },
        };
        var result = TaskDependencyChecker.DetectCycle(z, new[] { y }, allDeps);
        Assert.True(result.HasCycle);
    }

    [Fact]
    public void Proposed_parent_without_existing_dependencies_passes()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        // B has no entry in allDeps yet → can't form cycle
        var result = TaskDependencyChecker.DetectCycle(a, new[] { b },
            new Dictionary<Guid, IReadOnlyList<Guid>>());
        Assert.False(result.HasCycle);
    }
}
