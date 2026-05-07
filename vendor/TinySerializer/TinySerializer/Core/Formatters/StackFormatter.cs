using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Formatters;
using TinySerializer.Core.Misc;
using TinySerializer.Core.Serializers;
using TinySerializer.Utilities.Extensions;
using TinySerializer.Utilities.Misc;

[assembly: RegisterFormatter(typeof(StackFormatter<,>), weakFallback: typeof(WeakStackFormatter))]

namespace TinySerializer.Core.Formatters {
    public class StackFormatter<TStack, TValue> : BaseFormatter<TStack> where TStack : Stack<TValue>, new() {
        private static readonly Serializer<TValue> TSerializer = Serializer.Get<TValue>();
        private static readonly bool IsPlainStack = typeof(TStack) == typeof(Stack<TValue>);
        
        static StackFormatter() {
            
            new StackFormatter<Stack<int>, int>();
        }
        
        public StackFormatter() { }
        
        protected override TStack GetUninitializedObject() {
            return null;
        }
        
        protected override void DeserializeImplementation(ref TStack value, IDataReader reader) {
            string name;
            EntryType entry = reader.PeekEntry(out name);
            
            if (entry == EntryType.StartOfArray) {
                try {
                    long length;
                    reader.EnterArray(out length);
                    
                    if (IsPlainStack) {
                        value = (TStack)new Stack<TValue>((int)length);
                    } else {
                        value = new TStack();
                    }
                    
                    RegisterReferenceID(value, reader);
                    
                    for (int i = 0; i < length; i++) {
                        if (reader.PeekEntry(out name) == EntryType.EndOfArray) {
                            reader.Context.Config.DebugContext.LogError("Reached end of array after " + i + " elements, when " + length + " elements were expected.");
                            break;
                        }
                        
                        value.Push(TSerializer.ReadValue(reader));
                        
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
        
        protected override void SerializeImplementation(ref TStack value, IDataWriter writer) {
            try {
                writer.BeginArrayNode(value.Count);
                
                using (Cache<List<TValue>> listCache = Cache<List<TValue>>.Claim()) {
                    List<TValue> list = listCache.Value;
                    list.Clear();
                    
                    foreach (TValue element in value) {
                        list.Add(element);
                    }
                    
                    for (int i = list.Count - 1; i >= 0; i--) {
                        try {
                            TSerializer.WriteValue(list[i], writer);
                        } catch (Exception ex) {
                            writer.Context.Config.DebugContext.LogException(ex);
                        }
                    }
                }
            } finally {
                writer.EndArrayNode();
            }
        }
    }
    
    public class WeakStackFormatter : WeakBaseFormatter {
        private readonly Serializer ElementSerializer;
        private readonly bool IsPlainStack;
        private readonly MethodInfo PushMethod;
        
        public WeakStackFormatter(Type serializedType) : base(serializedType) {
            Type[] args = serializedType.GetArgumentsOfInheritedOpenGenericClass(typeof(Stack<>));
            ElementSerializer = Serializer.Get(args[0]);
            IsPlainStack = serializedType.IsGenericType && serializedType.GetGenericTypeDefinition() == typeof(Stack<>);
            
            if (PushMethod == null) {
                throw new SerializationAbortException("Can't serialize type '" + serializedType.GetNiceFullName() + "' because no proper Push method was found.");
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
                    
                    if (IsPlainStack) {
                        value = Activator.CreateInstance(SerializedType, (int)length);
                    } else {
                        value = Activator.CreateInstance(SerializedType);
                    }
                    
                    RegisterReferenceID(value, reader);
                    
                    object[] pushParams = new object[1];
                    
                    for (int i = 0; i < length; i++) {
                        if (reader.PeekEntry(out name) == EntryType.EndOfArray) {
                            reader.Context.Config.DebugContext.LogError("Reached end of array after " + i + " elements, when " + length + " elements were expected.");
                            break;
                        }
                        
                        pushParams[0] = ElementSerializer.ReadValueWeak(reader);
                        PushMethod.Invoke(value, pushParams);
                        
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
                
                using (Cache<List<object>> listCache = Cache<List<object>>.Claim()) {
                    List<object> list = listCache.Value;
                    list.Clear();
                    
                    foreach (object element in collection) {
                        list.Add(element);
                    }
                    
                    for (int i = list.Count - 1; i >= 0; i--) {
                        try {
                            ElementSerializer.WriteValueWeak(list[i], writer);
                        } catch (Exception ex) {
                            writer.Context.Config.DebugContext.LogException(ex);
                        }
                    }
                }
            } finally {
                writer.EndArrayNode();
            }
        }
    }
}