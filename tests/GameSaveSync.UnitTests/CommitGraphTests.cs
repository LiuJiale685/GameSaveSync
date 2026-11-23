using GameSaveSync.Core.Models;
using GameSaveSync.Core.Services;
using Xunit;

namespace GameSaveSync.UnitTests;

public class CommitGraphTests
{
    private static CommitInfo Commit(
        string id, string[] parentIds, string createdAt = "2026-01-01T00:00:00Z") =>
        new()
        {
            CommitId = id,
            GameId = "game-1",
            DeviceId = "device-1",
            ParentIds = parentIds.ToList(),
            ManifestHash = new string('a', 64),
            CreatedAtUtc = createdAt,
            Message = null,
        };

    [Fact]
    public void Analyze_链式提交_只有一个Head且无Pending()
    {
        var c1 = Commit("c1", [], "2026-01-01T00:00:01Z");
        var c2 = Commit("c2", ["c1"], "2026-01-01T00:00:02Z");
        var c3 = Commit("c3", ["c2"], "2026-01-01T00:00:03Z");

        var analysis = CommitGraph.Analyze([c1, c2, c3]);

        var head = Assert.Single(analysis.Heads);
        Assert.Equal("c3", head.CommitId);
        Assert.False(analysis.HasMultipleHeads);
        Assert.Empty(analysis.PendingNodes);
        Assert.True(analysis.Nodes["c1"].IsReferencedAsParent);
        Assert.True(analysis.Nodes["c2"].IsReferencedAsParent);
        Assert.False(analysis.Nodes["c3"].IsReferencedAsParent);
    }

    [Fact]
    public void Analyze_分叉提交_产生两个Head()
    {
        var c1 = Commit("c1", [], "2026-01-01T00:00:01Z");
        var c2a = Commit("c2a", ["c1"], "2026-01-01T00:00:02Z");
        var c2b = Commit("c2b", ["c1"], "2026-01-01T00:00:03Z");

        var analysis = CommitGraph.Analyze([c1, c2a, c2b]);

        Assert.True(analysis.HasMultipleHeads);
        Assert.Equal(2, analysis.Heads.Count);
        Assert.Contains(analysis.Heads, h => h.CommitId == "c2a");
        Assert.Contains(analysis.Heads, h => h.CommitId == "c2b");
        Assert.Empty(analysis.PendingNodes);
    }

    [Fact]
    public void Analyze_父版本缺失_进入PendingNodes且不算Head()
    {
        var c1 = Commit("c1", [], "2026-01-01T00:00:01Z");
        var c2 = Commit("c2", ["missing-parent"], "2026-01-01T00:00:02Z");

        var analysis = CommitGraph.Analyze([c1, c2]);

        var pending = Assert.Single(analysis.PendingNodes);
        Assert.Equal("c2", pending.Commit.CommitId);
        Assert.Contains("missing-parent", pending.MissingParents);
        // c2 等待缺失父版本不算 Head；c1 是唯一有效 Head。
        var head = Assert.Single(analysis.Heads);
        Assert.Equal("c1", head.CommitId);
        Assert.False(analysis.HasMultipleHeads);
    }

    [Fact]
    public void Analyze_父版本部分缺失_仍进入PendingNodes()
    {
        var c1 = Commit("c1", [], "2026-01-01T00:00:01Z");
        var c2 = Commit("c2", ["c1", "missing-parent"], "2026-01-01T00:00:02Z");

        var analysis = CommitGraph.Analyze([c1, c2]);

        var pending = Assert.Single(analysis.PendingNodes);
        Assert.Equal("c2", pending.Commit.CommitId);
        Assert.Empty(analysis.Heads);
    }

    [Fact]
    public void IsDescendantOf_链式_后代可达()
    {
        var c1 = Commit("c1", []);
        var c2 = Commit("c2", ["c1"]);
        var c3 = Commit("c3", ["c2"]);
        var analysis = CommitGraph.Analyze([c1, c2, c3]);

        Assert.True(CommitGraph.IsDescendantOf(analysis.Nodes, "c3", "c1"));
        Assert.True(CommitGraph.IsDescendantOf(analysis.Nodes, "c2", "c1"));
        Assert.True(CommitGraph.IsDescendantOf(analysis.Nodes, "c1", "c1")); // 含自身
        Assert.False(CommitGraph.IsDescendantOf(analysis.Nodes, "c1", "c3")); // 方向相反
        Assert.False(CommitGraph.IsDescendantOf(analysis.Nodes, "c1", "unknown")); // 未知祖先
    }

    [Fact]
    public void IsDescendantOf_合并节点_双分支都可达()
    {
        var c1 = Commit("c1", []);
        var c2a = Commit("c2a", ["c1"]);
        var c2b = Commit("c2b", ["c1"]);
        var merge = Commit("merge", ["c2a", "c2b"]);
        var analysis = CommitGraph.Analyze([c1, c2a, c2b, merge]);

        Assert.True(CommitGraph.IsDescendantOf(analysis.Nodes, "merge", "c2a"));
        Assert.True(CommitGraph.IsDescendantOf(analysis.Nodes, "merge", "c2b"));
        Assert.True(CommitGraph.IsDescendantOf(analysis.Nodes, "merge", "c1"));
    }

    [Fact]
    public void CollectAncestors_包含全部祖先与自身()
    {
        var c1 = Commit("c1", []);
        var c2 = Commit("c2", ["c1"]);
        var c3 = Commit("c3", ["c2"]);
        var analysis = CommitGraph.Analyze([c1, c2, c3]);

        var ancestors = CommitGraph.CollectAncestors(analysis.Nodes, "c3");

        Assert.Equal(new HashSet<string> { "c1", "c2", "c3" }, ancestors);
    }
}
