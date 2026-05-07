using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Misc;

namespace TinySerializer.Core.Serializers {
    public sealed class DecimalSerializer : Serializer<decimal> {
        public override decimal ReadValue(IDataReader reader) {
            string name;
            EntryType entry = reader.PeekEntry(out name);
            
            if (entry == EntryType.FloatingPoint || entry == EntryType.Integer) {
                decimal value;
                
                if (reader.ReadDecimal(out value) == false) {
                    reader.Context.Config.DebugContext.LogWarning("Failed to read entry of type " + entry.ToString());
                }
                
                return value;
            } else {
                reader.Context.Config.DebugContext.LogWarning("Expected entry of type " + EntryType.FloatingPoint.ToString() + " or " + EntryType.Integer.ToString()
                                                              + ", but got entry of type " + entry.ToString());
                
                reader.SkipEntry();
                return default(decimal);
            }
        }
        
        public override void WriteValue(string name, decimal value, IDataWriter writer) {
            FireOnSerializedType();
            writer.WriteDecimal(name, value);
        }
    }
}