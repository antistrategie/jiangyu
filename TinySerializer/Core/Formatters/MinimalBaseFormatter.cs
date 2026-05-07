using System;
using System.Runtime.Serialization;
using TinySerializer.Core.DataReaderWriters;

namespace TinySerializer.Core.Formatters {
    public abstract class MinimalBaseFormatter<T> : IFormatter<T> {
        protected static readonly bool IsValueType = typeof(T).IsValueType;
        
        public Type SerializedType { get { return typeof(T); } }
        
        public T Deserialize(IDataReader reader) {
            T result = GetUninitializedObject();
            
            if (IsValueType == false && ReferenceEquals(result, null) == false) {
                RegisterReferenceID(result, reader);
            }
            
            Read(ref result, reader);
            return result;
        }
        
        public void Serialize(T value, IDataWriter writer) {
            Write(ref value, writer);
        }
        
        void IFormatter.Serialize(object value, IDataWriter writer) {
            if (value is T) {
                Serialize((T)value, writer);
            }
        }
        
        object IFormatter.Deserialize(IDataReader reader) {
            return Deserialize(reader);
        }
        
        protected virtual T GetUninitializedObject() {
            if (IsValueType) {
                return default(T);
            } else {
                return (T)FormatterServices.GetUninitializedObject(typeof(T));
            }
        }
        
        protected abstract void Read(ref T value, IDataReader reader);
        
        protected abstract void Write(ref T value, IDataWriter writer);
        
        protected void RegisterReferenceID(T value, IDataReader reader) {
            if (!IsValueType) {
                int id = reader.CurrentNodeId;
                
                if (id < 0) {
                    reader.Context.Config.DebugContext.LogWarning(
                        "Reference type node is missing id upon deserialization. Some references may be broken. This tends to happen if a value type has changed to a reference type (IE, struct to class) since serialization took place.");
                } else {
                    reader.Context.RegisterInternalReference(id, value);
                }
            }
        }
    }
    
    public abstract class WeakMinimalBaseFormatter : IFormatter {
        protected readonly Type SerializedType;
        protected readonly bool IsValueType;
        
        Type IFormatter.SerializedType { get { return SerializedType; } }
        
        public WeakMinimalBaseFormatter(Type serializedType) {
            SerializedType = serializedType;
            IsValueType = SerializedType.IsValueType;
        }
        
        public object Deserialize(IDataReader reader) {
            object result = GetUninitializedObject();
            
            if (IsValueType == false && ReferenceEquals(result, null) == false) {
                RegisterReferenceID(result, reader);
            }
            
            Read(ref result, reader);
            return result;
        }
        
        public void Serialize(object value, IDataWriter writer) {
            Write(ref value, writer);
        }
        
        protected virtual object GetUninitializedObject() {
            if (IsValueType) {
                return Activator.CreateInstance(SerializedType);
            } else {
                return FormatterServices.GetUninitializedObject(SerializedType);
            }
        }
        
        protected abstract void Read(ref object value, IDataReader reader);
        
        protected abstract void Write(ref object value, IDataWriter writer);
        
        protected void RegisterReferenceID(object value, IDataReader reader) {
            if (!IsValueType) {
                int id = reader.CurrentNodeId;
                
                if (id < 0) {
                    reader.Context.Config.DebugContext.LogWarning(
                        "Reference type node is missing id upon deserialization. Some references may be broken. This tends to happen if a value type has changed to a reference type (IE, struct to class) since serialization took place.");
                } else {
                    reader.Context.RegisterInternalReference(id, value);
                }
            }
        }
    }
}