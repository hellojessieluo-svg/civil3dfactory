using System;
using System.Collections.Generic;

namespace Civil3DFactory
{
    /// <summary>
    /// Factory node catalog reader. Formal pipelines may only call nodes registered in nodes/node.json.
    /// Plain legacy ops work orders are still executed through Ops.Registry for compatibility.
    /// Contract parsing is centralised in NodeCatalog (the ribbon UI shares the same copy).
    /// </summary>
    public static class NodeRegistry
    {
        public static void EnsureRegistered(string nodeId)
        {
            if (string.IsNullOrWhiteSpace(nodeId))
                throw new InvalidOperationException("Pipeline station has no node field.");

            HashSet<string> ids = NodeCatalog.Ids();
            if (!ids.Contains(nodeId))
                throw new InvalidOperationException(
                    "Node '" + nodeId + "' is not registered in nodes/node.json.");
        }
    }
}
