using System;
using System.Reflection;

namespace TinySerializer.Core.Misc {
    public class CustomSerializationPolicy : ISerializationPolicy {
        private string id;
        private bool allowNonSerializableTypes;
        private Func<MemberInfo, bool> shouldSerializeFunc;
        
        public CustomSerializationPolicy(string id, bool allowNonSerializableTypes, Func<MemberInfo, bool> shouldSerializeFunc) {
            if (id == null) {
                throw new ArgumentNullException("id");
            }
            
            if (shouldSerializeFunc == null) {
                throw new ArgumentNullException("shouldSerializeFunc");
            }
            
            this.id = id;
            this.allowNonSerializableTypes = allowNonSerializableTypes;
            this.shouldSerializeFunc = shouldSerializeFunc;
        }
        
        public string ID { get { return id; } }
        
        public bool AllowNonSerializableTypes { get { return allowNonSerializableTypes; } }
        
        public bool ShouldSerializeMember(MemberInfo member) {
            return shouldSerializeFunc(member);
        }
    }
}