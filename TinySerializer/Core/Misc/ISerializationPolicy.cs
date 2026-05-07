using System.Reflection;

namespace TinySerializer.Core.Misc {
    public interface ISerializationPolicy {
        string ID { get; }
        
        bool AllowNonSerializableTypes { get; }
        
        bool ShouldSerializeMember(MemberInfo member);
    }
}