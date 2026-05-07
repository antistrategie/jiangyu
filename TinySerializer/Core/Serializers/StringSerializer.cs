using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Misc;

namespace TinySerializer.Core.Serializers {
    public sealed class StringSerializer : Serializer<string> {
        public override string ReadValue(IDataReader reader) {
            string name;
            EntryType entry = reader.PeekEntry(out name);
            
            if (entry == EntryType.String) {
                string value;
                
                if (reader.ReadString(out value) == false) {
                    reader.Context.Config.DebugContext.LogWarning("Failed to read entry '" + name + "' of type " + entry.ToString());
                }
                
                return value;
            } else if (entry == EntryType.Null) {
                if (reader.ReadNull() == false) {
                    reader.Context.Config.DebugContext.LogWarning("Failed to read entry '" + name + "' of type " + entry.ToString());
                }
                
                return null;
            } else {
                reader.Context.Config.DebugContext.LogWarning("Expected entry of type " + EntryType.String.ToString() + " or " + EntryType.Null.ToString() + ", but got entry '"
                                                              + name + "' of type " + entry.ToString());
                
                reader.SkipEntry();
                return default(string);
            }
        }
        
        public override void WriteValue(string name, string value, IDataWriter writer) {
            FireOnSerializedType();
            
            if (value == null) {
                writer.WriteNull(name);
            } else {
                writer.WriteString(name, value);
            }
        }
    }
}