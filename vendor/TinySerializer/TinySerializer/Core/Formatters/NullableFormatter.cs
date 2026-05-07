using System;
using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Formatters;
using TinySerializer.Core.Misc;
using TinySerializer.Core.Serializers;

[assembly: RegisterFormatter(typeof(NullableFormatter<>), weakFallback: typeof(WeakNullableFormatter))]

namespace TinySerializer.Core.Formatters {
    public sealed class NullableFormatter<T> : BaseFormatter<T?> where T : struct {
        private static readonly Serializer<T> TSerializer = Serializer.Get<T>();
        
        static NullableFormatter() {
            new NullableFormatter<int>();
        }
        
        public NullableFormatter() { }
        
        protected override void DeserializeImplementation(ref T? value, IDataReader reader) {
            string name;
            EntryType entry = reader.PeekEntry(out name);
            
            if (entry == EntryType.Null) {
                value = null;
                reader.ReadNull();
            } else {
                value = TSerializer.ReadValue(reader);
            }
        }
        
        protected override void SerializeImplementation(ref T? value, IDataWriter writer) {
            if (value.HasValue) {
                TSerializer.WriteValue(value.Value, writer);
            } else {
                writer.WriteNull(null);
            }
        }
    }
    
    public sealed class WeakNullableFormatter : WeakBaseFormatter {
        private readonly Serializer ValueSerializer;
        
        public WeakNullableFormatter(Type nullableType) : base(nullableType) {
            Type[] args = nullableType.GetGenericArguments();
            ValueSerializer = Serializer.Get(args[0]);
        }
        
        protected override void DeserializeImplementation(ref object value, IDataReader reader) {
            string name;
            EntryType entry = reader.PeekEntry(out name);
            
            if (entry == EntryType.Null) {
                value = null;
                reader.ReadNull();
            } else {
                value = ValueSerializer.ReadValueWeak(reader);
            }
        }
        
        protected override void SerializeImplementation(ref object value, IDataWriter writer) {
            if (value != null) {
                ValueSerializer.WriteValueWeak(value, writer);
            } else {
                writer.WriteNull(null);
            }
        }
        
        protected override object GetUninitializedObject() {
            return null;
        }
    }
}