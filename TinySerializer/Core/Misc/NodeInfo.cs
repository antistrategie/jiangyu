using System;

namespace TinySerializer.Core.Misc {
    public struct NodeInfo {
        public static readonly NodeInfo Empty = new NodeInfo(true);
        
        public readonly string Name;
        public readonly int Id;
        public readonly Type Type;
        public readonly bool IsArray;
        public readonly bool IsEmpty;
        
        public NodeInfo(string name, int id, Type type, bool isArray) {
            Name = name;
            Id = id;
            Type = type;
            IsArray = isArray;
            IsEmpty = false;
        }
        
        private NodeInfo(bool parameter) {
            Name = null;
            Id = -1;
            Type = null;
            IsArray = false;
            IsEmpty = true;
        }
        
        public static bool operator ==(NodeInfo a, NodeInfo b) {
            return a.Name == b.Name && a.Id == b.Id && a.Type == b.Type && a.IsArray == b.IsArray && a.IsEmpty == b.IsEmpty;
        }
        
        public static bool operator !=(NodeInfo a, NodeInfo b) {
            return !(a == b);
        }
        
        public override bool Equals(object obj) {
            if (ReferenceEquals(obj, null)) {
                return false;
            }
            
            if (obj is NodeInfo) {
                return (NodeInfo)obj == this;
            }
            
            return false;
        }
        
        public override int GetHashCode() {
            if (IsEmpty) {
                return 0;
            }
            
            const int P = 16777619;
            
            unchecked {
                return (int)2166136261 ^ ((Name == null ? 12321 : Name.GetHashCode()) * P) ^ (Id * P) ^ ((Type == null ? 1423 : Type.GetHashCode()) * P)
                    ^ ((IsArray ? 124124 : 43234) * P) ^ ((IsEmpty ? 872934 : 27323) * P);
            }
        }
    }
}