// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using GameSaveSync.Core.Models;

namespace GameSaveSync.Core.Services;

/// <summary>
/// Commit DAG 分析（方案 3.6 / 7.x）：Head 计算、祖先/后代判断、并行提交检测。
/// 不使用共享 current.json，通过扫描全部 Commit 计算。
/// </summary>
public static class CommitGraph
{
    /// <summary>节点状态。</summary>
    public enum NodeState
    {
        /// <summary>其 ParentIds 引用的 Commit 尚未全部同步到本机，暂缓判定。</summary>
        PendingParents = 0,

        /// <summary>依赖齐备，可参与 Head 判定。</summary>
        Complete = 1,
    }

    public sealed class GraphNode
    {
        public CommitInfo Commit { get; init; } = null!;

        public NodeState State { get; set; }

        /// <summary>是否被其他已知 Commit 作为父版本引用。</summary>
        public bool IsReferencedAsParent { get; set; }

        /// <summary>缺失的父 CommitId 列表（State = PendingParents 时非空）。</summary>
        public List<string> MissingParents { get; init; } = new();
    }

    public sealed class GraphAnalysis
    {
        public IReadOnlyDictionary<string, GraphNode> Nodes { get; init; } =
            new Dictionary<string, GraphNode>();

        /// <summary>有效 Head（依赖齐备且未被任何已知 Commit 引用为父版本）。</summary>
        public IReadOnlyList<CommitInfo> Heads { get; init; } = Array.Empty<CommitInfo>();

        /// <summary>父版本未到齐的 Commit（等待同步）。</summary>
        public IReadOnlyList<GraphNode> PendingNodes { get; init; } = Array.Empty<GraphNode>();

        public bool HasMultipleHeads => Heads.Count > 1;
    }

    public static GraphAnalysis Analyze(IEnumerable<CommitInfo> commits)
    {
        var byId = new Dictionary<string, GraphNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var commit in commits)
        {
            byId[commit.CommitId] = new GraphNode { Commit = commit, State = NodeState.Complete };
        }

        var pending = new List<GraphNode>();
        foreach (var node in byId.Values)
        {
            var missing = node.Commit.ParentIds
                .Where(p => !byId.ContainsKey(p))
                .ToList();
            if (missing.Count > 0)
            {
                node.State = NodeState.PendingParents;
                node.MissingParents.AddRange(missing);
                pending.Add(node);
            }
        }

        foreach (var node in byId.Values)
        {
            foreach (var parent in node.Commit.ParentIds)
            {
                if (byId.TryGetValue(parent, out var parentNode))
                {
                    parentNode.IsReferencedAsParent = true;
                }
            }
        }

        // Head：依赖齐备、且没有被任何已知 Commit 引用为父版本。
        var heads = byId.Values
            .Where(n => n.State == NodeState.Complete && !n.IsReferencedAsParent)
            .Select(n => n.Commit)
            .OrderBy(c => c.CreatedAtUtc, StringComparer.Ordinal)
            .ToList();

        return new GraphAnalysis
        {
            Nodes = byId,
            Heads = heads,
            PendingNodes = pending.OrderBy(n => n.Commit.CreatedAtUtc, StringComparer.Ordinal).ToList(),
        };
    }

    /// <summary>判断 candidate 是否是 ancestorId 的后代（可达，含自身）。</summary>
    public static bool IsDescendantOf(
        IReadOnlyDictionary<string, GraphNode> nodes,
        string candidateId,
        string ancestorId)
    {
        if (string.Equals(candidateId, ancestorId, StringComparison.OrdinalIgnoreCase))
            return true;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(candidateId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current))
                continue;
            if (string.Equals(current, ancestorId, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!nodes.TryGetValue(current, out var node))
                continue; // 父版本未同步到本机：不可达，按否处理
            foreach (var parent in node.Commit.ParentIds)
            {
                queue.Enqueue(parent);
            }
        }

        return false;
    }

    /// <summary>从基线 Commit 到 head 的所有祖先 CommitId（含基线自身）。</summary>
    public static HashSet<string> CollectAncestors(
        IReadOnlyDictionary<string, GraphNode> nodes,
        string commitId)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(commitId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!result.Add(current))
                continue;
            if (nodes.TryGetValue(current, out var node))
            {
                foreach (var parent in node.Commit.ParentIds)
                {
                    queue.Enqueue(parent);
                }
            }
        }

        return result;
    }
}
