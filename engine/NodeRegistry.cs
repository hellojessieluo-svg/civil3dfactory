using System;
using System.Collections.Generic;

namespace Civil3DFactory
{
    /// <summary>
    /// 工厂节点目录读取器。正式流水线只允许调用 nodes/node.json 中登记的节点。
    /// 普通旧式 ops 工单仍由 Ops.Registry 兼容执行。
    /// 契约解析统一由 NodeCatalog 负责（功能区界面共用同一份）。
    /// </summary>
    public static class NodeRegistry
    {
        public static void EnsureRegistered(string nodeId)
        {
            if (string.IsNullOrWhiteSpace(nodeId))
                throw new InvalidOperationException("流水线工位缺少 node 字段。");

            HashSet<string> ids = NodeCatalog.Ids();
            if (!ids.Contains(nodeId))
                throw new InvalidOperationException(
                    "节点 '" + nodeId + "' 未登记在 nodes/node.json。");
        }
    }
}
