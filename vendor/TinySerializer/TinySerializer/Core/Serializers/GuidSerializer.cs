using System;
using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Misc;

namespace TinySerializer.Core.Serializers {
    public sealed class GuidSerializer : Serializer<Guid> {
        public override Guid ReadValue(IDataReader reader) {
            string name;
            EntryType entry = reader.PeekEntry(out name);
            
            if (entry == EntryType.Guid) {
                Guid value;
                
                if (reader.ReadGuid(out value) == false) {
                    reader.Context.Config.DebugContext.LogWarning("Failed to read entry '" + name + "' of type " + entry.ToString());
                }
                
                return value;
            } else {
                reader.Context.Config.DebugContext.LogWarning("Expected entry of type " + EntryType.Guid.ToString() + ", but got entry '" + name + "' of type " + entry.ToString());
                reader.SkipEntry();
                return default(Guid);
            }
        }
        
        public override void WriteValue(string name, Guid value, IDataWriter writer) {
            FireOnSerializedType();
            writer.WriteGuid(name, value);
        }
    }
}