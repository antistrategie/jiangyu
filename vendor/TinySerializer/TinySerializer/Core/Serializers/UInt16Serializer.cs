using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Misc;

namespace TinySerializer.Core.Serializers {
    public sealed class UInt16Serializer : Serializer<ushort> {
        public override ushort ReadValue(IDataReader reader) {
            string name;
            EntryType entry = reader.PeekEntry(out name);
            
            if (entry == EntryType.Integer) {
                ushort value;
                
                if (reader.ReadUInt16(out value) == false) {
                    reader.Context.Config.DebugContext.LogWarning("Failed to read entry '" + name + "' of type " + entry.ToString());
                }
                
                return value;
            } else {
                reader.Context.Config.DebugContext.LogWarning("Expected entry of type " + EntryType.Integer.ToString() + ", but got entry '" + name + "' of type "
                                                              + entry.ToString());
                
                reader.SkipEntry();
                return default(ushort);
            }
        }
        
        public override void WriteValue(string name, ushort value, IDataWriter writer) {
            FireOnSerializedType();
            writer.WriteUInt16(name, value);
        }
    }
}