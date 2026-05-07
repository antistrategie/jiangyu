using System;
using System.Collections;
using System.Collections.Generic;
using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Formatters;
using TinySerializer.Core.Misc;
using TinySerializer.Core.Serializers;
using TinySerializer.Utilities.Extensions;
using TinySerializer.Utilities.Misc;

[assembly: RegisterFormatter(typeof(DoubleLookupDictionaryFormatter<,,>), weakFallback: typeof(WeakDoubleLookupDictionaryFormatter))]

namespace TinySerializer.Core.Formatters {
    public sealed class DoubleLookupDictionaryFormatter<TPrimary, TSecondary, TValue> : BaseFormatter<DoubleLookupDictionary<TPrimary, TSecondary, TValue>> {
        private static readonly Serializer<TPrimary> PrimaryReaderWriter = Serializer.Get<TPrimary>();
        private static readonly Serializer<Dictionary<TSecondary, TValue>> InnerReaderWriter = Serializer.Get<Dictionary<TSecondary, TValue>>();
        
        static DoubleLookupDictionaryFormatter() {
            new DoubleLookupDictionaryFormatter<int, int, string>();
        }
        
        public DoubleLookupDictionaryFormatter() { }
        
        protected override DoubleLookupDictionary<TPrimary, TSecondary, TValue> GetUninitializedObject() {
            return null;
        }
        
        protected override void SerializeImplementation(ref DoubleLookupDictionary<TPrimary, TSecondary, TValue> value, IDataWriter writer) {
            try {
                writer.BeginArrayNode(value.Count);
                
                bool endNode = true;
                
                foreach (KeyValuePair<TPrimary, Dictionary<TSecondary, TValue>> pair in value) {
                    try {
                        writer.BeginStructNode(null, null);
                        PrimaryReaderWriter.WriteValue("$k", pair.Key, writer);
                        InnerReaderWriter.WriteValue("$v", pair.Value, writer);
                    } catch (SerializationAbortException ex) {
                        endNode = false;
                        throw ex;
                    } catch (Exception ex) {
                        writer.Context.Config.DebugContext.LogException(ex);
                    } finally {
                        if (endNode) {
                            writer.EndNode(null);
                        }
                    }
                }
            } finally {
                writer.EndArrayNode();
            }
        }
        
        protected override void DeserializeImplementation(ref DoubleLookupDictionary<TPrimary, TSecondary, TValue> value, IDataReader reader) {
            string name;
            EntryType entry = reader.PeekEntry(out name);
            
            if (entry == EntryType.StartOfArray) {
                try {
                    long length;
                    reader.EnterArray(out length);
                    Type type;
                    value = new DoubleLookupDictionary<TPrimary, TSecondary, TValue>();
                    
                    RegisterReferenceID(value, reader);
                    
                    for (int i = 0; i < length; i++) {
                        if (reader.PeekEntry(out name) == EntryType.EndOfArray) {
                            reader.Context.Config.DebugContext.LogError("Reached end of array after " + i + " elements, when " + length + " elements were expected.");
                            break;
                        }
                        
                        bool exitNode = true;
                        
                        try {
                            reader.EnterNode(out type);
                            TPrimary key = PrimaryReaderWriter.ReadValue(reader);
                            Dictionary<TSecondary, TValue> inner = InnerReaderWriter.ReadValue(reader);
                            
                            value.Add(key, inner);
                        } catch (SerializationAbortException ex) {
                            exitNode = false;
                            throw ex;
                        } catch (Exception ex) {
                            reader.Context.Config.DebugContext.LogException(ex);
                        } finally {
                            if (exitNode) {
                                reader.ExitNode();
                            }
                        }
                        
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
    }
    
    internal sealed class WeakDoubleLookupDictionaryFormatter : WeakBaseFormatter {
        private readonly Serializer PrimaryReaderWriter;
        private readonly Serializer InnerReaderWriter;
        
        public WeakDoubleLookupDictionaryFormatter(Type serializedType) : base(serializedType) {
            Type[] args = serializedType.GetArgumentsOfInheritedOpenGenericClass(typeof(Dictionary<,>));
            
            PrimaryReaderWriter = Serializer.Get(args[0]);
            InnerReaderWriter = Serializer.Get(args[1]);
        }
        
        protected override object GetUninitializedObject() {
            return null;
        }
        
        protected override void SerializeImplementation(ref object value, IDataWriter writer) {
            try {
                IDictionary dict = (IDictionary)value;
                writer.BeginArrayNode(dict.Count);
                
                bool endNode = true;
                IDictionaryEnumerator enumerator = dict.GetEnumerator();
                
                try {
                    while (enumerator.MoveNext()) {
                        try {
                            writer.BeginStructNode(null, null);
                            PrimaryReaderWriter.WriteValueWeak("$k", enumerator.Key, writer);
                            InnerReaderWriter.WriteValueWeak("$v", enumerator.Value, writer);
                        } catch (SerializationAbortException ex) {
                            endNode = false;
                            throw ex;
                        } catch (Exception ex) {
                            writer.Context.Config.DebugContext.LogException(ex);
                        } finally {
                            if (endNode) {
                                writer.EndNode(null);
                            }
                        }
                    }
                } finally {
                    enumerator.Reset();
                    IDisposable dispose = enumerator as IDisposable;
                    if (dispose != null) dispose.Dispose();
                }
            } finally {
                writer.EndArrayNode();
            }
        }
        
        protected override void DeserializeImplementation(ref object value, IDataReader reader) {
            string name;
            EntryType entry = reader.PeekEntry(out name);
            
            if (entry == EntryType.StartOfArray) {
                try {
                    long length;
                    reader.EnterArray(out length);
                    Type type;
                    value = Activator.CreateInstance(SerializedType);
                    IDictionary dict = (IDictionary)value;
                    
                    RegisterReferenceID(value, reader);
                    
                    for (int i = 0; i < length; i++) {
                        if (reader.PeekEntry(out name) == EntryType.EndOfArray) {
                            reader.Context.Config.DebugContext.LogError("Reached end of array after " + i + " elements, when " + length + " elements were expected.");
                            break;
                        }
                        
                        bool exitNode = true;
                        
                        try {
                            reader.EnterNode(out type);
                            object key = PrimaryReaderWriter.ReadValueWeak(reader);
                            object inner = InnerReaderWriter.ReadValueWeak(reader);
                            
                            dict.Add(key, inner);
                        } catch (SerializationAbortException ex) {
                            exitNode = false;
                            throw ex;
                        } catch (Exception ex) {
                            reader.Context.Config.DebugContext.LogException(ex);
                        } finally {
                            if (exitNode) {
                                reader.ExitNode();
                            }
                        }
                        
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
    }
}