using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Misc;

namespace TinySerializer.Core.Serializers {
    public sealed class ByteSerializer : Serializer<byte> {
        public override byte ReadValue(IDataReader reader) {
            string name;
            EntryType entry = reader.PeekEntry(out name);
            
            if (entry == EntryType.Integer) {
                byte value;
                
                if (reader.ReadByte(out value) == false) {
                    reader.Context.Config.DebugContext.LogWarning("Failed to read entry '" + name + "' of type " + entry.ToString());
                }
                
                return value;
            } else {
                reader.Context.Config.DebugContext.LogWarning("Expected entry of type " + EntryType.Integer.ToString() + ", but got entry '" + name + "' of type "
                                                              + entry.ToString());
                
                reader.SkipEntry();
                return default(byte);
            }
        }
        
        public override void WriteValue(string name, byte value, IDataWriter writer) {
            FireOnSerializedType();
            writer.WriteByte(name, value);
        }
    }
}