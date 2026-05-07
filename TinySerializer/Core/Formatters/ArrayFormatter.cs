using System;
using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Misc;
using TinySerializer.Core.Serializers;

namespace TinySerializer.Core.Formatters {
    public sealed class ArrayFormatter<T> : BaseFormatter<T[]> {
        private static Serializer<T> valueReaderWriter = Serializer.Get<T>();
        
        protected override T[] GetUninitializedObject() {
            return null;
        }
        
        protected override void DeserializeImplementation(ref T[] value, IDataReader reader) {
            string name;
            EntryType entry = reader.PeekEntry(out name);
            
            if (entry == EntryType.StartOfArray) {
                long length;
                reader.EnterArray(out length);
                
                value = new T[length];
                
                RegisterReferenceID(value, reader);
                
                for (int i = 0; i < length; i++) {
                    if (reader.PeekEntry(out name) == EntryType.EndOfArray) {
                        reader.Context.Config.DebugContext.LogError("Reached end of array after " + i + " elements, when " + length + " elements were expected.");
                        break;
                    }
                    
                    value[i] = valueReaderWriter.ReadValue(reader);
                    
                    if (reader.PeekEntry(out name) == EntryType.EndOfStream) {
                        break;
                    }
                }
                
                reader.ExitArray();
            } else {
                reader.SkipEntry();
            }
        }
        
        protected override void SerializeImplementation(ref T[] value, IDataWriter writer) {
            try {
                writer.BeginArrayNode(value.Length);
                
                for (int i = 0; i < value.Length; i++) {
                    valueReaderWriter.WriteValue(value[i], writer);
                }
            } finally {
                writer.EndArrayNode();
            }
        }
    }
    
    public sealed class WeakArrayFormatter : WeakBaseFormatter {
        private readonly Serializer ValueReaderWriter;
        private readonly Type ElementType;
        
        public WeakArrayFormatter(Type arrayType, Type elementType) : base(arrayType) {
            ValueReaderWriter = Serializer.Get(elementType);
            ElementType = elementType;
        }
        
        protected override object GetUninitializedObject() {
            return null;
        }
        
        protected override void DeserializeImplementation(ref object value, IDataReader reader) {
            string name;
            EntryType entry = reader.PeekEntry(out name);
            
            if (entry == EntryType.StartOfArray) {
                long length;
                reader.EnterArray(out length);
                
                Array array = Array.CreateInstance(ElementType, length);
                value = array;
                
                RegisterReferenceID(value, reader);
                
                for (int i = 0; i < length; i++) {
                    if (reader.PeekEntry(out name) == EntryType.EndOfArray) {
                        reader.Context.Config.DebugContext.LogError("Reached end of array after " + i + " elements, when " + length + " elements were expected.");
                        break;
                    }
                    
                    array.SetValue(ValueReaderWriter.ReadValueWeak(reader), i);
                    
                    if (reader.PeekEntry(out name) == EntryType.EndOfStream) {
                        break;
                    }
                }
                
                reader.ExitArray();
            } else {
                reader.SkipEntry();
            }
        }
        
        protected override void SerializeImplementation(ref object value, IDataWriter writer) {
            Array array = (Array)value;
            
            try {
                int length = array.Length;
                writer.BeginArrayNode(length);
                
                for (int i = 0; i < length; i++) {
                    ValueReaderWriter.WriteValueWeak(array.GetValue(i), writer);
                }
            } finally {
                writer.EndArrayNode();
            }
        }
    }
}