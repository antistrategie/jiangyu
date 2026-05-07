using System;
using TinySerializer.Core.Misc;

namespace TinySerializer.Core.DataReaderWriters {
    public abstract class BaseDataReaderWriter {
        private NodeInfo[] nodes = new NodeInfo[32];
        private int nodesLength = 0;
        
        [Obsolete("Use the Binder member on the writer's SerializationContext/DeserializationContext instead.", error: false)]
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public TwoWaySerializationBinder Binder {
            get {
                if (this is IDataWriter) {
                    return (this as IDataWriter).Context.Binder;
                } else if (this is IDataReader) {
                    return (this as IDataReader).Context.Binder;
                }
                
                return TwoWaySerializationBinder.Default;
            }
            
            set {
                if (this is IDataWriter) {
                    (this as IDataWriter).Context.Binder = value;
                } else if (this is IDataReader) {
                    (this as IDataReader).Context.Binder = value;
                }
            }
        }
        
        public bool IsInArrayNode { get { return nodesLength == 0 ? false : nodes[nodesLength - 1].IsArray; } }
        
        protected int NodeDepth { get { return nodesLength; } }
        
        protected NodeInfo[] NodesArray { get { return nodes; } }
        
        protected NodeInfo CurrentNode { get { return nodesLength == 0 ? NodeInfo.Empty : nodes[nodesLength - 1]; } }
        
        protected void PushNode(NodeInfo node) {
            if (nodesLength == nodes.Length) {
                ExpandNodes();
            }
            
            nodes[nodesLength] = node;
            nodesLength++;
        }
        
        protected void PushNode(string name, int id, Type type) {
            if (nodesLength == nodes.Length) {
                ExpandNodes();
            }
            
            nodes[nodesLength] = new NodeInfo(name, id, type, false);
            nodesLength++;
        }
        
        protected void PushArray() {
            if (nodesLength == nodes.Length) {
                ExpandNodes();
            }
            
            if (nodesLength == 0 || nodes[nodesLength - 1].IsArray) {
                nodes[nodesLength] = new NodeInfo(null, -1, null, true);
            } else {
                NodeInfo current = nodes[nodesLength - 1];
                nodes[nodesLength] = new NodeInfo(current.Name, current.Id, current.Type, true);
            }
            
            nodesLength++;
        }
        
        private void ExpandNodes() {
            NodeInfo[] newArr = new NodeInfo[nodes.Length * 2];
            
            NodeInfo[] oldNodes = nodes;
            
            for (int i = 0; i < oldNodes.Length; i++) {
                newArr[i] = oldNodes[i];
            }
            
            nodes = newArr;
        }
        
        protected void PopNode(string name) {
            if (nodesLength == 0) {
                throw new InvalidOperationException("There are no nodes to pop.");
            }
                
            nodesLength--;
        }
        
        protected void PopArray() {
            if (nodesLength == 0) {
                throw new InvalidOperationException("There are no nodes to pop.");
            }
            
            if (nodes[nodesLength - 1].IsArray == false) {
                throw new InvalidOperationException("Was not in array when exiting array.");
            }
            
            nodesLength--;
        }
        
        protected void ClearNodes() {
            nodesLength = 0;
        }
    }
}