using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Misc;

namespace TinySerializer.Core.Serializers {
    public sealed class UInt32Serializer : Serializer<uint> {
        public override uint ReadValue(IDataReader reader) {
            string name;
            EntryType entry = reader.PeekEntry(out name);
            
            if (entry == EntryType.Integer) {
                uint value;
                
                if (reader.ReadUInt32(out value) == false) {
                    reader.Context.Config.DebugContext.LogWarning("Failed to read entry '" + name + "' of type " + entry.ToString());
                }
                
                return value;
            } else {
                reader.Context.Config.DebugContext.LogWarning("Expected entry of type " + EntryType.Integer.ToString() + ", but got entry '" + name + "' of type "
                                                              + entry.ToString());
                
                reader.SkipEntry();
                return default(uint);
            }
        }
        
        public override void WriteValue(string name, uint value, IDataWriter writer) {
            FireOnSerializedType();
            writer.WriteUInt32(name, value);
        }
    }
}