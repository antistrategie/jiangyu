using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Misc;

namespace TinySerializer.Core.Serializers {
    public sealed class BooleanSerializer : Serializer<bool> {
        public override bool ReadValue(IDataReader reader) {
            string name;
            EntryType entry = reader.PeekEntry(out name);
            
            if (entry == EntryType.Boolean) {
                bool value;
                
                if (reader.ReadBoolean(out value) == false) {
                    reader.Context.Config.DebugContext.LogWarning("Failed to read entry '" + name + "' of type " + entry.ToString());
                }
                
                return value;
            } else {
                reader.Context.Config.DebugContext.LogWarning("Expected entry of type " + EntryType.Boolean.ToString() + ", but got entry '" + name + "' of type "
                                                              + entry.ToString());
                
                reader.SkipEntry();
                return default(bool);
            }
        }
        
        public override void WriteValue(string name, bool value, IDataWriter writer) {
            FireOnSerializedType();
            writer.WriteBoolean(name, value);
        }
    }
}