using System;
using System.Reflection;
using System.Runtime.Serialization;
using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Misc;
using TinySerializer.Core.Serializers;

namespace TinySerializer.Core.Formatters {
    public sealed class SerializableFormatter<T> : BaseFormatter<T> where T : ISerializable {
        private static readonly Func<SerializationInfo, StreamingContext, T> ISerializableConstructor;
        private static readonly ReflectionFormatter<T> ReflectionFormatter;
        
        static SerializableFormatter() {
            Type current = typeof(T);
            
            ConstructorInfo constructor = null;
            
            do {
                constructor = current.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
                                                     new Type[] { typeof(SerializationInfo), typeof(StreamingContext) }, null);
                
                current = current.BaseType;
            } while (constructor == null
                && current != typeof(object)
                && current != null);
            
            if (constructor != null) {
                ISerializableConstructor = (info, context) =>
                {
                    T obj = (T)FormatterServices.GetUninitializedObject(typeof(T));
                    constructor.Invoke(obj, new object[] { info, context });
                    return obj;
                };
            } else {
                DefaultLoggers.DefaultLogger.LogWarning("Type " + typeof(T).Name
                                                        + " implements the interface ISerializable but does not implement the required constructor with signature " + typeof(T).Name
                                                        + "(SerializationInfo info, StreamingContext context). The interface declaration will be ignored, and the formatter fallbacks to reflection.");
                
                ReflectionFormatter = new ReflectionFormatter<T>();
            }
        }
        
        protected override T GetUninitializedObject() {
            return default(T);
        }
        
        protected override void DeserializeImplementation(ref T value, IDataReader reader) {
            if (ISerializableConstructor != null) {
                SerializationInfo info = ReadSerializationInfo(reader);
                
                if (info != null) {
                    try {
                        value = ISerializableConstructor(info, reader.Context.StreamingContext);
                        
                        InvokeOnDeserializingCallbacks(ref value, reader.Context);
                        
                        if (IsValueType == false) {
                            RegisterReferenceID(value, reader);
                        }
                        
                        return;
                    } catch (Exception ex) {
                        reader.Context.Config.DebugContext.LogException(ex);
                    }
                }
            } else {
                value = ReflectionFormatter.Deserialize(reader);
                
                InvokeOnDeserializingCallbacks(ref value, reader.Context);
                
                if (IsValueType == false) {
                    RegisterReferenceID(value, reader);
                }
            }
        }
        
        protected override void SerializeImplementation(ref T value, IDataWriter writer) {
            if (ISerializableConstructor != null) {
                
                ISerializable serializable = value as ISerializable;
                SerializationInfo info = new SerializationInfo((value as object).GetType(), writer.Context.FormatterConverter);
                
                try {
                    serializable.GetObjectData(info, writer.Context.StreamingContext);
                } catch (Exception ex) {
                    writer.Context.Config.DebugContext.LogException(ex);
                }
                
                WriteSerializationInfo(info, writer);
            } else {
                ReflectionFormatter.Serialize(value, writer);
            }
        }
        
        private SerializationInfo ReadSerializationInfo(IDataReader reader) {
            string name;
            EntryType entry = reader.PeekEntry(out name);
            
            if (entry == EntryType.StartOfArray) {
                try {
                    long length;
                    reader.EnterArray(out length);
                    
                    SerializationInfo info = new SerializationInfo(typeof(T), reader.Context.FormatterConverter);
                    
                    for (int i = 0; i < length; i++) {
                        Type type = null;
                        entry = reader.PeekEntry(out name);
                        
                        if (entry == EntryType.String && name == "type") {
                            string typeName;
                            reader.ReadString(out typeName);
                            type = reader.Context.Binder.BindToType(typeName, reader.Context.Config.DebugContext);
                        }
                        
                        if (type == null) {
                            reader.SkipEntry();
                            continue;
                        }
                        
                        entry = reader.PeekEntry(out name);
                        
                        Serializer readerWriter = Serializer.Get(type);
                        object value = readerWriter.ReadValueWeak(reader);
                        info.AddValue(name, value);
                    }
                    
                    return info;
                } finally {
                    reader.ExitArray();
                }
            }
            
            return null;
        }
        
        private void WriteSerializationInfo(SerializationInfo info, IDataWriter writer) {
            try {
                writer.BeginArrayNode(info.MemberCount);
                
                foreach (SerializationEntry entry in info) {
                    try {
                        writer.WriteString("type", writer.Context.Binder.BindToName(entry.ObjectType, writer.Context.Config.DebugContext));
                        Serializer readerWriter = Serializer.Get(entry.ObjectType);
                        readerWriter.WriteValueWeak(entry.Name, entry.Value, writer);
                    } catch (Exception ex) {
                        writer.Context.Config.DebugContext.LogException(ex);
                    }
                }
            } finally {
                writer.EndArrayNode();
            }
        }
    }
    
    public sealed class WeakSerializableFormatter : WeakBaseFormatter {
        private readonly Func<SerializationInfo, StreamingContext, ISerializable> ISerializableConstructor;
        private readonly WeakReflectionFormatter ReflectionFormatter;
        
        public WeakSerializableFormatter(Type serializedType) : base(serializedType) {
            Type current = serializedType;
            ConstructorInfo constructor = null;
            
            do {
                constructor = current.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
                                                     new Type[] { typeof(SerializationInfo), typeof(StreamingContext) }, null);
                
                current = current.BaseType;
            } while (constructor == null
                && current != typeof(object)
                && current != null);
            
            if (constructor != null) {
                ISerializableConstructor = (info, context) =>
                {
                    ISerializable obj = (ISerializable)FormatterServices.GetUninitializedObject(SerializedType);
                    constructor.Invoke(obj, new object[] { info, context });
                    return obj;
                };
            } else {
                DefaultLoggers.DefaultLogger.LogWarning("Type " + SerializedType.Name
                                                        + " implements the interface ISerializable but does not implement the required constructor with signature "
                                                        + SerializedType.Name
                                                        + "(SerializationInfo info, StreamingContext context). The interface declaration will be ignored, and the formatter fallbacks to reflection.");
                
                ReflectionFormatter = new WeakReflectionFormatter(SerializedType);
            }
        }
        
        protected override object GetUninitializedObject() {
            return null;
        }
        
        protected override void DeserializeImplementation(ref object value, IDataReader reader) {
            if (ISerializableConstructor != null) {
                SerializationInfo info = ReadSerializationInfo(reader);
                
                if (info != null) {
                    try {
                        value = ISerializableConstructor(info, reader.Context.StreamingContext);
                        
                        InvokeOnDeserializingCallbacks(value, reader.Context);
                        
                        if (IsValueType == false) {
                            RegisterReferenceID(value, reader);
                        }
                        
                        return;
                    } catch (Exception ex) {
                        reader.Context.Config.DebugContext.LogException(ex);
                    }
                }
            } else {
                value = ReflectionFormatter.Deserialize(reader);
                
                InvokeOnDeserializingCallbacks(value, reader.Context);
                
                if (IsValueType == false) {
                    RegisterReferenceID(value, reader);
                }
            }
        }
        
        protected override void SerializeImplementation(ref object value, IDataWriter writer) {
            if (ISerializableConstructor != null) {
                ISerializable serializable = value as ISerializable;
                SerializationInfo info = new SerializationInfo(value.GetType(), writer.Context.FormatterConverter);
                
                try {
                    serializable.GetObjectData(info, writer.Context.StreamingContext);
                } catch (Exception ex) {
                    writer.Context.Config.DebugContext.LogException(ex);
                }
                
                WriteSerializationInfo(info, writer);
            } else {
                ReflectionFormatter.Serialize(value, writer);
            }
        }
        
        private SerializationInfo ReadSerializationInfo(IDataReader reader) {
            string name;
            EntryType entry = reader.PeekEntry(out name);
            
            if (entry == EntryType.StartOfArray) {
                try {
                    long length;
                    reader.EnterArray(out length);
                    
                    SerializationInfo info = new SerializationInfo(SerializedType, reader.Context.FormatterConverter);
                    
                    for (int i = 0; i < length; i++) {
                        Type type = null;
                        entry = reader.PeekEntry(out name);
                        
                        if (entry == EntryType.String && name == "type") {
                            string typeName;
                            reader.ReadString(out typeName);
                            type = reader.Context.Binder.BindToType(typeName, reader.Context.Config.DebugContext);
                        }
                        
                        if (type == null) {
                            reader.SkipEntry();
                            continue;
                        }
                        
                        entry = reader.PeekEntry(out name);
                        
                        Serializer readerWriter = Serializer.Get(type);
                        object value = readerWriter.ReadValueWeak(reader);
                        info.AddValue(name, value);
                    }
                    
                    return info;
                } finally {
                    reader.ExitArray();
                }
            }
            
            return null;
        }
        
        private void WriteSerializationInfo(SerializationInfo info, IDataWriter writer) {
            try {
                writer.BeginArrayNode(info.MemberCount);
                
                foreach (SerializationEntry entry in info) {
                    try {
                        writer.WriteString("type", writer.Context.Binder.BindToName(entry.ObjectType, writer.Context.Config.DebugContext));
                        Serializer readerWriter = Serializer.Get(entry.ObjectType);
                        readerWriter.WriteValueWeak(entry.Name, entry.Value, writer);
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