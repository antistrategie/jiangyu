using System;
using System.Collections.Generic;
using System.Reflection;
using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Formatters;
using TinySerializer.Core.Misc;
using TinySerializer.Core.Serializers;

[assembly: RegisterFormatter(typeof(KeyValuePairFormatter<,>), weakFallback: typeof(WeakKeyValuePairFormatter))]

namespace TinySerializer.Core.Formatters {
    public sealed class KeyValuePairFormatter<TKey, TValue> : BaseFormatter<KeyValuePair<TKey, TValue>> {
        private static readonly Serializer<TKey> KeySerializer = Serializer.Get<TKey>();
        private static readonly Serializer<TValue> ValueSerializer = Serializer.Get<TValue>();
        
        protected override void SerializeImplementation(ref KeyValuePair<TKey, TValue> value, IDataWriter writer) {
            KeySerializer.WriteValue(value.Key, writer);
            ValueSerializer.WriteValue(value.Value, writer);
        }
        
        protected override void DeserializeImplementation(ref KeyValuePair<TKey, TValue> value, IDataReader reader) {
            value = new KeyValuePair<TKey, TValue>(KeySerializer.ReadValue(reader), ValueSerializer.ReadValue(reader));
        }
    }
    
    public sealed class WeakKeyValuePairFormatter : WeakBaseFormatter {
        private readonly Serializer KeySerializer;
        private readonly Serializer ValueSerializer;
        
        private readonly PropertyInfo KeyProperty;
        private readonly PropertyInfo ValueProperty;
        
        public WeakKeyValuePairFormatter(Type serializedType) : base(serializedType) {
            Type[] args = serializedType.GetGenericArguments();
            
            KeySerializer = Serializer.Get(args[0]);
            ValueSerializer = Serializer.Get(args[1]);
            
            KeyProperty = serializedType.GetProperty("Key");
            ValueProperty = serializedType.GetProperty("Value");
        }
        
        protected override void SerializeImplementation(ref object value, IDataWriter writer) {
            KeySerializer.WriteValueWeak(KeyProperty.GetValue(value, null), writer);
            ValueSerializer.WriteValueWeak(ValueProperty.GetValue(value, null), writer);
        }
        
        protected override void DeserializeImplementation(ref object value, IDataReader reader) {
            value = Activator.CreateInstance(SerializedType, KeySerializer.ReadValueWeak(reader), ValueSerializer.ReadValueWeak(reader));
        }
    }
}