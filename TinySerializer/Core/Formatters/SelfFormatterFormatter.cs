using System;
using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Misc;

namespace TinySerializer.Core.Formatters {
    public sealed class SelfFormatterFormatter<T> : BaseFormatter<T> where T : ISelfFormatter {
        protected override void DeserializeImplementation(ref T value, IDataReader reader) {
            value.Deserialize(reader);
        }
        
        protected override void SerializeImplementation(ref T value, IDataWriter writer) {
            value.Serialize(writer);
        }
    }
    
    public sealed class WeakSelfFormatterFormatter : WeakBaseFormatter {
        public WeakSelfFormatterFormatter(Type serializedType) : base(serializedType) { }
        
        protected override void DeserializeImplementation(ref object value, IDataReader reader) {
            ((ISelfFormatter)value).Deserialize(reader);
        }
        
        protected override void SerializeImplementation(ref object value, IDataWriter writer) {
            ((ISelfFormatter)value).Serialize(writer);
        }
    }
}