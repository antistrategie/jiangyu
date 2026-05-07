using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Misc;

namespace TinySerializer.Core.Serializers {
    public sealed class SByteSerializer : Serializer<sbyte> {
        public override sbyte ReadValue(IDataReader reader) {
            string name;
            EntryType entry = reader.PeekEntry(out name);
            
            if (entry == EntryType.Integer) {
                sbyte value;
                
                if (reader.ReadSByte(out value) == false) {
                    reader.Context.Config.DebugContext.LogWarning("Failed to read entry '" + name + "' of type " + entry.ToString());
                }
                
                return value;
            } else {
                reader.Context.Config.DebugContext.LogWarning("Expected entry of type " + EntryType.Integer.ToString() + ", but got entry '" + name + "' of type "
                                                              + entry.ToString());
                
                reader.SkipEntry();
                return default(sbyte);
            }
        }
        
        public override void WriteValue(string name, sbyte value, IDataWriter writer) {
            FireOnSerializedType();
            writer.WriteSByte(name, value);
        }
    }
}