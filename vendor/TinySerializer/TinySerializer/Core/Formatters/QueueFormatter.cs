using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Formatters;
using TinySerializer.Core.Misc;
using TinySerializer.Core.Serializers;
using TinySerializer.Utilities.Extensions;

[assembly: RegisterFormatter(typeof(QueueFormatter<,>), weakFallback: typeof(WeakQueueFormatter))]

namespace TinySerializer.Core.Formatters {
    public class QueueFormatter<TQueue, TValue> : BaseFormatter<TQueue> where TQueue : Queue<TValue>, new() {
        private static readonly Serializer<TValue> TSerializer = Serializer.Get<TValue>();
        private static readonly bool IsPlainQueue = typeof(TQueue) == typeof(Queue<TValue>);
        
        static QueueFormatter() {
            
            new QueueFormatter<Queue<int>, int>();
        }
        
        public QueueFormatter() { }
        
        protected override TQueue GetUninitializedObject() {
            return null;
        }
        
        protected override void DeserializeImplementation(ref TQueue value, IDataReader reader) {
            string name;
            EntryType entry = reader.PeekEntry(out name);
            
            if (entry == EntryType.StartOfArray) {
                try {
                    long length;
                    reader.EnterArray(out length);
                    
                    if (IsPlainQueue) {
                        value = (TQueue)new Queue<TValue>((int)length);
                    } else {
                        value = new TQueue();
                    }
                    
                    RegisterReferenceID(value, reader);
                    
                    for (int i = 0; i < length; i++) {
                        if (reader.PeekEntry(out name) == EntryType.EndOfArray) {
                            reader.Context.Config.DebugContext.LogError("Reached end of array after " + i + " elements, when " + length + " elements were expected.");
                            break;
                        }
                        
                        value.Enqueue(TSerializer.ReadValue(reader));
                        
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
        
        protected override void SerializeImplementation(ref TQueue value, IDataWriter writer) {
            try {
                writer.BeginArrayNode(value.Count);
                
                foreach (TValue element in value) {
                    try {
                        TSerializer.WriteValue(element, writer);
                    } catch (Exception ex) {
                        writer.Context.Config.DebugContext.LogException(ex);
                    }
                }
            } finally {
                writer.EndArrayNode();
            }
        }
    }
    
    public class WeakQueueFormatter : WeakBaseFormatter {
        private readonly Serializer ElementSerializer;
        private readonly bool IsPlainQueue;
        private MethodInfo EnqueueMethod;
        
        public WeakQueueFormatter(Type serializedType) : base(serializedType) {
            Type[] args = serializedType.GetArgumentsOfInheritedOpenGenericClass(typeof(Queue<>));
            ElementSerializer = Serializer.Get(args[0]);
            IsPlainQueue = serializedType.IsGenericType && serializedType.GetGenericTypeDefinition() == typeof(Queue<>);
            EnqueueMethod = serializedType.GetMethod("Enqueue", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic, null, new Type[] { args[0] }, null);
            
            if (EnqueueMethod == null) {
                throw new SerializationAbortException("Can't serialize type '" + serializedType.GetNiceFullName() + "' because no proper Enqueue method was found.");
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
                    
                    if (IsPlainQueue) {
                        value = Activator.CreateInstance(SerializedType, (int)length);
                    } else {
                        value = Activator.CreateInstance(SerializedType);
                    }
                    
                    ICollection collection = (ICollection)value;
                    
                    RegisterReferenceID(value, reader);
                    
                    object[] enqueueParams = new object[1];
                    
                    for (int i = 0; i < length; i++) {
                        if (reader.PeekEntry(out name) == EntryType.EndOfArray) {
                            reader.Context.Config.DebugContext.LogError("Reached end of array after " + i + " elements, when " + length + " elements were expected.");
                            break;
                        }
                        
                        enqueueParams[0] = ElementSerializer.ReadValueWeak(reader);
                        EnqueueMethod.Invoke(value, enqueueParams);
                        
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
                ICollection collection = (ICollection)value;
                
                writer.BeginArrayNode(collection.Count);
                
                foreach (object element in collection) {
                    try {
                        ElementSerializer.WriteValueWeak(element, writer);
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