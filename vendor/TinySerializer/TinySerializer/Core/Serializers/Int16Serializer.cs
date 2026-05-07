using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Misc;

namespace TinySerializer.Core.Serializers {
    public sealed class Int16Serializer : Serializer<short> {
        public override short ReadValue(IDataReader reader) {
            string name;
            EntryType entry = reader.PeekEntry(out name);
            
            if (entry == EntryType.Integer) {
                short value;
                
                if (reader.ReadInt16(out value) == false) {
                    reader.Context.Config.DebugContext.LogWarning("Failed to read entry '" + name + "' of type " + entry.ToString());
                }
                
                return value;
            } else {
                reader.Context.Config.DebugContext.LogWarning("Expected entry of type " + EntryType.Integer.ToString() + ", but got entry '" + name + "' of type "
                                                              + entry.ToString());
                
                reader.SkipEntry();
                return default(short);
            }
        }
        
        public override void WriteValue(string name, short value, IDataWriter writer) {
            FireOnSerializedType();
            writer.WriteInt16(name, value);
        }
    }
}