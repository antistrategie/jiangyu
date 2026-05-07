using System;
using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Misc;

namespace TinySerializer.Core.Serializers {
    public sealed class EnumSerializer<T> : Serializer<T> {
        static EnumSerializer() {
            if (typeof(T).IsEnum == false) {
                throw new Exception($"Type {typeof(T).Name} is not an enum.");
            }
        }
        
        public override T ReadValue(IDataReader reader) {
            string name;
            EntryType entry = reader.PeekEntry(out name);
            
            if (entry == EntryType.Integer) {
                ulong value;
                
                if (reader.ReadUInt64(out value) == false) {
                    reader.Context.Config.DebugContext.LogWarning($"Failed to read entry '{name}' of type {entry}");
                }
                
                return (T)Enum.ToObject(typeof(T), value);
            } else {
                reader.Context.Config.DebugContext.LogWarning($"Expected entry of type {EntryType.Integer}, but got entry '{name}' of type {entry}");
                
                reader.SkipEntry();
                return default(T);
            }
        }
        
        public override void WriteValue(string name, T value, IDataWriter writer) {
            ulong ul;
            
            FireOnSerializedType();
            
            try {
                ul = Convert.ToUInt64(value as Enum);
            } catch (OverflowException) {
                unchecked {
                    ul = (ulong)Convert.ToInt64(value as Enum);
                }
            }
            
            writer.WriteUInt64(name, ul);
        }
    }
}