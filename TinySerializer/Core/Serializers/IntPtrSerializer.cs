using System;
using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Misc;

namespace TinySerializer.Core.Serializers {
    public sealed class IntPtrSerializer : Serializer<IntPtr> {
        public override IntPtr ReadValue(IDataReader reader) {
            string name;
            EntryType entry = reader.PeekEntry(out name);
            
            if (entry == EntryType.Integer) {
                long value;
                
                if (reader.ReadInt64(out value) == false) {
                    reader.Context.Config.DebugContext.LogWarning("Failed to read entry '" + name + "' of type " + entry.ToString());
                }
                
                return new IntPtr(value);
            } else {
                reader.Context.Config.DebugContext.LogWarning("Expected entry of type " + EntryType.Integer.ToString() + ", but got entry '" + name + "' of type "
                                                              + entry.ToString());
                
                reader.SkipEntry();
                return default(IntPtr);
            }
        }
        
        public override void WriteValue(string name, IntPtr value, IDataWriter writer) {
            FireOnSerializedType();
            writer.WriteInt64(name, (long)value);
        }
    }
}