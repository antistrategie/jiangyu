using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Misc;

namespace TinySerializer.Core.Serializers {
    public sealed class CharSerializer : Serializer<char> {
        public override char ReadValue(IDataReader reader) {
            string name;
            EntryType entry = reader.PeekEntry(out name);
            
            if (entry == EntryType.String) {
                char value;
                
                if (reader.ReadChar(out value) == false) {
                    reader.Context.Config.DebugContext.LogWarning("Failed to read entry '" + name + "' of type " + entry.ToString());
                }
                
                return value;
            } else {
                reader.Context.Config.DebugContext.LogWarning(
                    "Expected entry of type " + EntryType.String.ToString() + ", but got entry '" + name + "' of type " + entry.ToString());
                
                reader.SkipEntry();
                return default(char);
            }
        }
        
        public override void WriteValue(string name, char value, IDataWriter writer) {
            FireOnSerializedType();
            writer.WriteChar(name, value);
        }
    }
}