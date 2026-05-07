using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Formatters;
using TinySerializer.Core.Misc;
using TinySerializer.Core.Serializers;
using TinySerializer.Utilities.Extensions;

[assembly: RegisterFormatter(typeof(DictionaryFormatter<,>), weakFallback: typeof(WeakDictionaryFormatter))]

namespace TinySerializer.Core.Formatters {
    public sealed class DictionaryFormatter<TKey, TValue> : BaseFormatter<Dictionary<TKey, TValue>> {
        private static readonly bool KeyIsValueType = typeof(TKey).IsValueType;
        
        private static readonly Serializer<IEqualityComparer<TKey>> EqualityComparerSerializer = Serializer.Get<IEqualityComparer<TKey>>();
        private static readonly Serializer<TKey> KeyReaderWriter = Serializer.Get<TKey>();
        private static readonly Serializer<TValue> ValueReaderWriter = Serializer.Get<TValue>();
        
        static DictionaryFormatter() {
            new DictionaryFormatter<int, string>();
        }
        
        public DictionaryFormatter() { }
        
        protected override Dictionary<TKey, TValue> GetUninitializedObject() {
            return null;
        }
        
        protected override void DeserializeImplementation(ref Dictionary<TKey, TValue> value, IDataReader reader) {
            string name;
            EntryType entry = reader.PeekEntry(out name);
            
            IEqualityComparer<TKey> comparer = null;
            
            if (name == "comparer" || entry != EntryType.StartOfArray) {
                comparer = EqualityComparerSerializer.ReadValue(reader);
                entry = reader.PeekEntry(out name);
            }
            
            if (entry == EntryType.StartOfArray) {
                try {
                    long length;
                    reader.EnterArray(out length);
                    Type type;
                    
                    value = ReferenceEquals(comparer, null) ? new Dictionary<TKey, TValue>((int)length) : new Dictionary<TKey, TValue>((int)length, comparer);
                    
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
        
        /// <summary>
        /// Provides the actual implementation for serializing a value of type <see cref="T" />.
        /// </summary>
        /// <param name="value">The value to serialize.</param>
        /// <param name="writer">The writer to serialize with.</param>
        protected override void SerializeImplementation(ref Dictionary<TKey, TValue> value, IDataWriter writer) {
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
    
    internal sealed class WeakDictionaryFormatter : WeakBaseFormatter {
        private readonly bool KeyIsValueType;
        
        private readonly Serializer EqualityComparerSerializer;
        private readonly Serializer KeyReaderWriter;
        private readonly Serializer ValueReaderWriter;
        
        private readonly ConstructorInfo ComparerConstructor;
        private readonly PropertyInfo ComparerProperty;
        private readonly PropertyInfo CountProperty;
        private readonly Type KeyType;
        private readonly Type ValueType;
        
        public WeakDictionaryFormatter(Type serializedType) : base(serializedType) {
            Type[] args = serializedType.GetArgumentsOfInheritedOpenGenericClass(typeof(Dictionary<,>));
            
            KeyType = args[0];
            ValueType = args[1];
            KeyIsValueType = KeyType.IsValueType;
            KeyReaderWriter = Serializer.Get(KeyType);
            ValueReaderWriter = Serializer.Get(ValueType);
            
            CountProperty = serializedType.GetProperty("Count");
            
            if (CountProperty == null) {
                throw new SerializationAbortException("Can't serialize/deserialize the type " + serializedType.GetNiceFullName() + " because it has no accessible Count property.");
            }
            
            try {
                Type equalityComparerType = typeof(IEqualityComparer<>).MakeGenericType(KeyType);
                
                EqualityComparerSerializer = Serializer.Get(equalityComparerType);
                ComparerConstructor = serializedType.GetConstructor(new Type[] { equalityComparerType });
                ComparerProperty = serializedType.GetProperty("Comparer");
            } catch (Exception) {
                EqualityComparerSerializer = Serializer.Get<object>();
                ComparerConstructor = null;
                ComparerProperty = null;
            }
        }
        
        protected override object GetUninitializedObject() {
            return null;
        }
        
        protected override void DeserializeImplementation(ref object value, IDataReader reader) {
            string name;
            EntryType entry = reader.PeekEntry(out name);
            
            object comparer = null;
            
            if (name == "comparer" || entry == EntryType.StartOfNode) {
                comparer = EqualityComparerSerializer.ReadValueWeak(reader);
                entry = reader.PeekEntry(out name);
            }
            
            if (entry == EntryType.StartOfArray) {
                try {
                    long length;
                    reader.EnterArray(out length);
                    Type type;
                    
                    if (!ReferenceEquals(comparer, null) && ComparerConstructor != null) {
                        value = ComparerConstructor.Invoke(new object[] { comparer });
                    } else {
                        value = Activator.CreateInstance(SerializedType);
                    }
                    
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
                            object key = KeyReaderWriter.ReadValueWeak(reader);
                            object val = ValueReaderWriter.ReadValueWeak(reader);
                            
                            if (!KeyIsValueType && ReferenceEquals(key, null)) {
                                reader.Context.Config.DebugContext.LogWarning("Dictionary key of type '" + KeyType.FullName
                                                                              + "' was null upon deserialization. A key has gone missing.");
                                
                                continue;
                            }
                            
                            dict[key] = val;
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
        
        protected override void SerializeImplementation(ref object value, IDataWriter writer) {
            try {
                IDictionary dict = (IDictionary)value;
                
                if (ComparerProperty != null) {
                    object comparer = ComparerProperty.GetValue(value, null);
                    
                    if (!ReferenceEquals(comparer, null)) {
                        EqualityComparerSerializer.WriteValueWeak("comparer", comparer, writer);
                    }
                }
                
                writer.BeginArrayNode((int)CountProperty.GetValue(value, null));
                
                IDictionaryEnumerator enumerator = dict.GetEnumerator();
                
                try {
                    while (enumerator.MoveNext()) {
                        bool endNode = true;
                        
                        try {
                            writer.BeginStructNode(null, null);
                            KeyReaderWriter.WriteValueWeak("$k", enumerator.Key, writer);
                            ValueReaderWriter.WriteValueWeak("$v", enumerator.Value, writer);
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
    }
}