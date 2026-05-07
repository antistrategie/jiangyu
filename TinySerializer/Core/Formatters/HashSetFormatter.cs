using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Formatters;
using TinySerializer.Core.Misc;
using TinySerializer.Core.Serializers;
using TinySerializer.Utilities.Extensions;

[assembly: RegisterFormatter(typeof(HashSetFormatter<>), weakFallback: typeof(WeakHashSetFormatter))]

namespace TinySerializer.Core.Formatters {
    public class HashSetFormatter<T> : BaseFormatter<HashSet<T>> {
        private static readonly Serializer<T> TSerializer = Serializer.Get<T>();
        
        static HashSetFormatter() {
            new HashSetFormatter<int>();
        }
        
        public HashSetFormatter() { }
        
        protected override HashSet<T> GetUninitializedObject() {
            return null;
        }
        
        protected override void DeserializeImplementation(ref HashSet<T> value, IDataReader reader) {
            string name;
            EntryType entry = reader.PeekEntry(out name);
            
            if (entry == EntryType.StartOfArray) {
                try {
                    long length;
                    reader.EnterArray(out length);
                    value = new HashSet<T>();
                    
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
        
        protected override void SerializeImplementation(ref HashSet<T> value, IDataWriter writer) {
            try {
                writer.BeginArrayNode(value.Count);
                
                foreach (T item in value) {
                    try {
                        TSerializer.WriteValue(item, writer);
                    } catch (Exception ex) {
                        writer.Context.Config.DebugContext.LogException(ex);
                    }
                }
            } finally {
                writer.EndArrayNode();
            }
        }
    }
    
    public class WeakHashSetFormatter : WeakBaseFormatter {
        private readonly Serializer ElementSerializer;
        private readonly MethodInfo AddMethod;
        private readonly PropertyInfo CountProperty;
        
        public WeakHashSetFormatter(Type serializedType) : base(serializedType) {
            Type[] args = serializedType.GetArgumentsOfInheritedOpenGenericClass(typeof(HashSet<>));
            ElementSerializer = Serializer.Get(args[0]);
            
            AddMethod = serializedType.GetMethod("Add", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic, null, new Type[] { args[0] }, null);
            CountProperty = serializedType.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            
            if (AddMethod == null) {
                throw new SerializationAbortException("Can't serialize/deserialize hashset of type '" + serializedType.GetNiceFullName()
                                                      + "' since a proper Add method wasn't found.");
            }
            
            if (CountProperty == null) {
                throw new SerializationAbortException("Can't serialize/deserialize hashset of type '" + serializedType.GetNiceFullName()
                                                      + "' since a proper Count property wasn't found.");
            }
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
                    value = Activator.CreateInstance(SerializedType);
                    
                    RegisterReferenceID(value, reader);
                    
                    object[] addParams = new object[1];
                    
                    for (int i = 0; i < length; i++) {
                        if (reader.PeekEntry(out name) == EntryType.EndOfArray) {
                            reader.Context.Config.DebugContext.LogError("Reached end of array after " + i + " elements, when " + length + " elements were expected.");
                            break;
                        }
                        
                        addParams[0] = ElementSerializer.ReadValueWeak(reader);
                        AddMethod.Invoke(value, addParams);
                        
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
                writer.BeginArrayNode((int)CountProperty.GetValue(value, null));
                
                foreach (object item in ((IEnumerable)value)) {
                    try {
                        ElementSerializer.WriteValueWeak(item, writer);
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