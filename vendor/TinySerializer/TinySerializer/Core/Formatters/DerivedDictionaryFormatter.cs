using System;
using System.Collections.Generic;
using System.Reflection;
using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Formatters;
using TinySerializer.Core.Misc;
using TinySerializer.Core.Serializers;

[assembly: RegisterFormatter(typeof(DerivedDictionaryFormatter<,,>), weakFallback: typeof(WeakDictionaryFormatter), priority: -1)]

namespace TinySerializer.Core.Formatters {
    internal sealed class DerivedDictionaryFormatter<TDictionary, TKey, TValue> : BaseFormatter<TDictionary> where TDictionary : Dictionary<TKey, TValue>, new() {
        private static readonly bool KeyIsValueType = typeof(TKey).IsValueType;
        
        private static readonly Serializer<IEqualityComparer<TKey>> EqualityComparerSerializer = Serializer.Get<IEqualityComparer<TKey>>();
        private static readonly Serializer<TKey> KeyReaderWriter = Serializer.Get<TKey>();
        private static readonly Serializer<TValue> ValueReaderWriter = Serializer.Get<TValue>();
        
        private static readonly ConstructorInfo ComparerConstructor = typeof(TDictionary).GetConstructor(new Type[] { typeof(IEqualityComparer<TKey>) });
        
        static DerivedDictionaryFormatter() {
            
            new DerivedDictionaryFormatter<Dictionary<int, string>, int, string>();
        }
        
        public DerivedDictionaryFormatter() { }
        
        protected override TDictionary GetUninitializedObject() {
            return null;
        }
        
        protected override void DeserializeImplementation(ref TDictionary value, IDataReader reader) {
            string name;
            EntryType entry = reader.PeekEntry(out name);
            
            IEqualityComparer<TKey> comparer = null;
            
            if (name == "comparer" || entry == EntryType.StartOfNode) {
                comparer = EqualityComparerSerializer.ReadValue(reader);
                entry = reader.PeekEntry(out name);
            }
            
            if (entry == EntryType.StartOfArray) {
                try {
                    long length;
                    reader.EnterArray(out length);
                    Type type;
                    
                    if (!ReferenceEquals(comparer, null) && ComparerConstructor != null) {
                        value = (TDictionary)ComparerConstructor.Invoke(new object[] { comparer });
                    } else {
                        value = new TDictionary();
                    }
                    
                    RegisterReferenceID(value, reader);
                    
                    for (int i = 0; i < length; i++) {
                        if (reader.PeekEntry(out name) == EntryType.EndOfArray) {
                            reader.Context.Config.DebugContext.LogError("Reached end of array after " + i + " elements, when " + length + " elements were expected.");
                            break;
                        }
                        
                        bool exitNode = true;
                        
                        try {
                            reader.EnterNode(out type);
                            TKey key = KeyReaderWriter.ReadValue(reader);
                            TValue val = ValueReaderWriter.ReadValue(reader);
                            
                            if (!KeyIsValueType && ReferenceEquals(key, null)) {
                                reader.Context.Config.DebugContext.LogWarning("Dictionary key of type '" + typeof(TKey).FullName
                                                                              + "' was null upon deserialization. A key has gone missing.");
                                
                                continue;
                            }
                            
                            value[key] = val;
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
        
        protected override void SerializeImplementation(ref TDictionary value, IDataWriter writer) {
            try {
                if (value.Comparer != null) {
                    EqualityComparerSerializer.WriteValue("comparer", value.Comparer, writer);
                }
                
                writer.BeginArrayNode(value.Count);
                
                foreach (KeyValuePair<TKey, TValue> pair in value) {
                    bool endNode = true;
                    
                    try {
                        writer.BeginStructNode(null, null);
                        KeyReaderWriter.WriteValue("$k", pair.Key, writer);
                        ValueReaderWriter.WriteValue("$v", pair.Value, writer);
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
    }
}