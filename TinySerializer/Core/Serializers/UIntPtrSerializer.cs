using System;
using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Misc;

namespace TinySerializer.Core.Serializers {
    public sealed class UIntPtrSerializer : Serializer<UIntPtr> {
        public override UIntPtr ReadValue(IDataReader reader) {
            string name;
            EntryType entry = reader.PeekEntry(out name);
            
            if (entry == EntryType.Integer) {
                ulong value;
                
                if (reader.ReadUInt64(out value) == false) {
                    reader.Context.Config.DebugContext.LogWarning("Failed to read entry '" + name + "' of type " + entry.ToString());
                }
                
                return new UIntPtr(value);
            } else {
                reader.Context.Config.DebugContext.LogWarning("Expected entry of type " + EntryType.Integer.ToString() + ", but got entry '" + name + "' of type "
                                                              + entry.ToString());
                
                reader.SkipEntry();
                return default(UIntPtr);
            }
        }
        
        public override void WriteValue(string name, UIntPtr value, IDataWriter writer) {
            FireOnSerializedType();
            writer.WriteUInt64(name, (ulong)value);
        }
    }
}