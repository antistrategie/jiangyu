using System;
using System.Collections;
using System.Collections.Generic;
using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Formatters;
using TinySerializer.Core.Misc;
using TinySerializer.Core.Serializers;
using TinySerializer.Utilities.Extensions;

[assembly: RegisterFormatter(typeof(ListFormatter<>), weakFallback: typeof(WeakListFormatter))]

namespace TinySerializer.Core.Formatters {
    public class ListFormatter<T> : BaseFormatter<List<T>> {
        private static readonly Serializer<T> TSerializer = Serializer.Get<T>();
        
        static ListFormatter() {
            
            new ListFormatter<int>();
        }
        
        public ListFormatter() { }
        
        protected override List<T> GetUninitializedObject() {
            return null;
        }
        
        protected override void DeserializeImplementation(ref List<T> value, IDataReader reader) {
            string name;
            EntryType entry = reader.PeekEntry(out name);
            
            if (entry == EntryType.StartOfArray) {
                try {
                    long length;
                    reader.EnterArray(out length);
                    value = new List<T>((int)length);
                    
                    RegisterReferenceID(value, reader);
                    
                    for (int i = 0; i < length; i++) {
                        if (reader.PeekEntry(out name) == EntryType.EndOfArray) {
                            reader.Context.Config.DebugContext.LogError("Reached end of array after " + i + " elements, when " + length + " elements were expected.");
                            break;
                        }
                        
                        value.Add(TSerializer.ReadValue(reader));
                        
                        if (reader.IsInArrayNode == false) {
                            reader.Context.Config.DebugContext.LogError("Reading array went wrong. Data dump: " + reader.GetDataDump());
                            break;
                        }
                    }
                } finally {
                    reader.ExitArray();
                }
            } else {
                reader.SkipEntry();
            }
        }
        
        protected override void SerializeImplementation(ref List<T> value, IDataWriter writer) {
            try {
                writer.BeginArrayNode(value.Count);
                
                for (int i = 0; i < value.Count; i++) {
                    try {
                        TSerializer.WriteValue(value[i], writer);
                    } catch (Exception ex) {
                        writer.Context.Config.DebugContext.LogException(ex);
                    }
                }
            } finally {
                writer.EndArrayNode();
            }
        }
    }
    
    public class WeakListFormatter : WeakBaseFormatter {
        private readonly Serializer ElementSerializer;
        
        public WeakListFormatter(Type serializedType) : base(serializedType) {
            Type[] args = serializedType.GetArgumentsOfInheritedOpenGenericClass(typeof(List<>));
            ElementSerializer = Serializer.Get(args[0]);
        }
        
        protected override object GetUninitializedObject() {
            return null;
        }
        
        protected override void DeserializeImplementation(ref object value, IDataReader reader) {
            string name;
            EntryType entry = reader.PeekEntry(out name);
            
            if (entry == EntryType.StartOfArray) {
                try {
                    long length;
                    reader.EnterArray(out length);
                    value = Activator.CreateInstance(SerializedType, (int)length);
                    IList list = (IList)value;
                    
                    RegisterReferenceID(value, reader);
                    
                    for (int i = 0; i < length; i++) {
                        if (reader.PeekEntry(out name) == EntryType.EndOfArray) {
                            reader.Context.Config.DebugContext.LogError("Reached end of array after " + i + " elements, when " + length + " elements were expected.");
                            break;
                        }
                        
                        list.Add(ElementSerializer.ReadValueWeak(reader));
                        
                        if (reader.IsInArrayNode == false) {
                            reader.Context.Config.DebugContext.LogError("Reading array went wrong. Data dump: " + reader.GetDataDump());
                            break;
                        }
                    }
                } finally {
                    reader.ExitArray();
                }
            } else {
                reader.SkipEntry();
            }
        }
        
        protected override void SerializeImplementation(ref object value, IDataWriter writer) {
            try {
                IList list = (IList)value;
                writer.BeginArrayNode(list.Count);
                
                for (int i = 0; i < list.Count; i++) {
                    try {
                        ElementSerializer.WriteValueWeak(list[i], writer);
                    } catch (Exception ex) {
                        writer.Context.Config.DebugContext.LogException(ex);
                    }
                }
            } finally {
                writer.EndArrayNode();
            }
        }
    }
}