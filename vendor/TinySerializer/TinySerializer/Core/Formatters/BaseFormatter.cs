using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Misc;
using TinySerializer.Utilities.Extensions;

namespace TinySerializer.Core.Formatters {
    public abstract class BaseFormatter<T> : IFormatter<T> {
        protected delegate void SerializationCallback(ref T value, StreamingContext context);
        
        protected static readonly SerializationCallback[] OnSerializingCallbacks;
        protected static readonly SerializationCallback[] OnSerializedCallbacks;
        protected static readonly SerializationCallback[] OnDeserializingCallbacks;
        protected static readonly SerializationCallback[] OnDeserializedCallbacks;
        
        protected static readonly bool IsValueType = typeof(T).IsValueType;
        
        protected static readonly bool ImplementsIDeserializationCallback = typeof(T).ImplementsOrInherits(typeof(IDeserializationCallback));
        protected static readonly bool ImplementsIObjectReference = typeof(T).ImplementsOrInherits(typeof(IObjectReference));
        
        static BaseFormatter() {
            MethodInfo[] methods = typeof(T).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            
            List<SerializationCallback> callbacks = new List<SerializationCallback>();
            
            OnSerializingCallbacks = GetCallbacks(methods, typeof(OnSerializingAttribute), ref callbacks);
            OnSerializedCallbacks = GetCallbacks(methods, typeof(OnSerializedAttribute), ref callbacks);
            OnDeserializingCallbacks = GetCallbacks(methods, typeof(OnDeserializingAttribute), ref callbacks);
            OnDeserializedCallbacks = GetCallbacks(methods, typeof(OnDeserializedAttribute), ref callbacks);
        }
        
        private static SerializationCallback[] GetCallbacks(MethodInfo[] methods, Type callbackAttribute, ref List<SerializationCallback> list) {
            for (int i = 0; i < methods.Length; i++) {
                MethodInfo method = methods[i];
                
                if (method.IsDefined(callbackAttribute, true)) {
                    SerializationCallback callback = CreateCallback(method);
                    
                    if (callback != null) {
                        list.Add(callback);
                    }
                }
            }
            
            SerializationCallback[] result = list.ToArray();
            list.Clear();
            return result;
        }
        
        private static SerializationCallback CreateCallback(MethodInfo info) {
            ParameterInfo[] parameters = info.GetParameters();
            
            if (parameters.Length == 0) {
                return (ref T value, StreamingContext context) =>
                {
                    object obj = value;
                    info.Invoke(obj, null);
                    value = (T)obj;
                };
            } else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(StreamingContext) && parameters[0].ParameterType.IsByRef == false) {
                return (ref T value, StreamingContext context) =>
                {
                    object obj = value;
                    info.Invoke(obj, new object[] { context });
                    value = (T)obj;
                };
            } else {
                DefaultLoggers.DefaultLogger.LogWarning("The method " + info.GetNiceName() + " has an invalid signature and will be ignored by the serialization system.");
                return null;
            }
        }
        
        public Type SerializedType { get { return typeof(T); } }
        
        void IFormatter.Serialize(object value, IDataWriter writer) {
            Serialize((T)value, writer);
        }
        
        object IFormatter.Deserialize(IDataReader reader) {
            return Deserialize(reader);
        }
        
        public T Deserialize(IDataReader reader) {
            DeserializationContext context = reader.Context;
            T value = GetUninitializedObject();
            
            if (IsValueType) {
                InvokeOnDeserializingCallbacks(ref value, context);
            } else {
                if (ReferenceEquals(value, null) == false) {
                    RegisterReferenceID(value, reader);
                    InvokeOnDeserializingCallbacks(ref value, context);
                    
                    if (ImplementsIObjectReference) {
                        try {
                            value = (T)(value as IObjectReference).GetRealObject(context.StreamingContext);
                            RegisterReferenceID(value, reader);
                        } catch (Exception ex) {
                            context.Config.DebugContext.LogException(ex);
                        }
                    }
                }
            }
            
            try {
                DeserializeImplementation(ref value, reader);
            } catch (Exception ex) {
                context.Config.DebugContext.LogException(ex);
            }
            
            if (IsValueType || ReferenceEquals(value, null) == false) {
                for (int i = 0; i < OnDeserializedCallbacks.Length; i++) {
                    try {
                        OnDeserializedCallbacks[i](ref value, context.StreamingContext);
                    } catch (Exception ex) {
                        context.Config.DebugContext.LogException(ex);
                    }
                }
                
                if (ImplementsIDeserializationCallback) {
                    IDeserializationCallback v = value as IDeserializationCallback;
                    v.OnDeserialization(this);
                    value = (T)v;
                }
            }
            
            return value;
        }
        
        public void Serialize(T value, IDataWriter writer) {
            SerializationContext context = writer.Context;
            
            for (int i = 0; i < OnSerializingCallbacks.Length; i++) {
                try {
                    OnSerializingCallbacks[i](ref value, context.StreamingContext);
                } catch (Exception ex) {
                    context.Config.DebugContext.LogException(ex);
                }
            }
            
            try {
                SerializeImplementation(ref value, writer);
            } catch (Exception ex) {
                context.Config.DebugContext.LogException(ex);
            }
            
            for (int i = 0; i < OnSerializedCallbacks.Length; i++) {
                try {
                    OnSerializedCallbacks[i](ref value, context.StreamingContext);
                } catch (Exception ex) {
                    context.Config.DebugContext.LogException(ex);
                }
            }
        }
        
        protected virtual T GetUninitializedObject() {
            if (IsValueType) {
                return default(T);
            } else {
                return (T)FormatterServices.GetUninitializedObject(typeof(T));
            }
        }
        
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
        
        [Obsolete("Use the InvokeOnDeserializingCallbacks variant that takes a ref T value instead. This is for struct compatibility reasons.", false)]
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        protected void InvokeOnDeserializingCallbacks(T value, DeserializationContext context) {
            InvokeOnDeserializingCallbacks(ref value, context);
        }
        
        protected void InvokeOnDeserializingCallbacks(ref T value, DeserializationContext context) {
            for (int i = 0; i < OnDeserializingCallbacks.Length; i++) {
                try {
                    OnDeserializingCallbacks[i](ref value, context.StreamingContext);
                } catch (Exception ex) {
                    context.Config.DebugContext.LogException(ex);
                }
            }
        }
        
        protected abstract void DeserializeImplementation(ref T value, IDataReader reader);
        
        protected abstract void SerializeImplementation(ref T value, IDataWriter writer);
    }
    
    public abstract class WeakBaseFormatter : IFormatter {
        protected delegate void SerializationCallback(object value, StreamingContext context);
        protected readonly Type SerializedType;
        
        protected readonly SerializationCallback[] OnSerializingCallbacks;
        protected readonly SerializationCallback[] OnSerializedCallbacks;
        protected readonly SerializationCallback[] OnDeserializingCallbacks;
        protected readonly SerializationCallback[] OnDeserializedCallbacks;
        protected readonly bool IsValueType;
        
        protected readonly bool ImplementsIDeserializationCallback;
        protected readonly bool ImplementsIObjectReference;
        
        Type IFormatter.SerializedType { get { return SerializedType; } }
        
        public WeakBaseFormatter(Type serializedType) {
            SerializedType = serializedType;
            ImplementsIDeserializationCallback = SerializedType.ImplementsOrInherits(typeof(IDeserializationCallback));
            ImplementsIObjectReference = SerializedType.ImplementsOrInherits(typeof(IObjectReference));
            
            MethodInfo[] methods = SerializedType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            
            List<SerializationCallback> callbacks = new List<SerializationCallback>();
            
            OnSerializingCallbacks = GetCallbacks(methods, typeof(OnSerializingAttribute), ref callbacks);
            OnSerializedCallbacks = GetCallbacks(methods, typeof(OnSerializedAttribute), ref callbacks);
            OnDeserializingCallbacks = GetCallbacks(methods, typeof(OnDeserializingAttribute), ref callbacks);
            OnDeserializedCallbacks = GetCallbacks(methods, typeof(OnDeserializedAttribute), ref callbacks);
        }
        
        private static SerializationCallback[] GetCallbacks(MethodInfo[] methods, Type callbackAttribute, ref List<SerializationCallback> list) {
            for (int i = 0; i < methods.Length; i++) {
                MethodInfo method = methods[i];
                
                if (method.IsDefined(callbackAttribute, true)) {
                    SerializationCallback callback = CreateCallback(method);
                    
                    if (callback != null) {
                        list.Add(callback);
                    }
                }
            }
            
            SerializationCallback[] result = list.ToArray();
            list.Clear();
            return result;
        }
        
        private static SerializationCallback CreateCallback(MethodInfo info) {
            ParameterInfo[] parameters = info.GetParameters();
            
            if (parameters.Length == 0) {
                return (object value, StreamingContext context) =>
                {
                    info.Invoke(value, null);
                };
            } else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(StreamingContext) && parameters[0].ParameterType.IsByRef == false) {
                return (object value, StreamingContext context) =>
                {
                    info.Invoke(value, new object[] { context });
                };
            } else {
                DefaultLoggers.DefaultLogger.LogWarning("The method " + info.GetNiceName() + " has an invalid signature and will be ignored by the serialization system.");
                return null;
            }
        }
        
        public void Serialize(object value, IDataWriter writer) {
            SerializationContext context = writer.Context;
            
            for (int i = 0; i < OnSerializingCallbacks.Length; i++) {
                try {
                    OnSerializingCallbacks[i](value, context.StreamingContext);
                } catch (Exception ex) {
                    context.Config.DebugContext.LogException(ex);
                }
            }
            
            try {
                SerializeImplementation(ref value, writer);
            } catch (Exception ex) {
                context.Config.DebugContext.LogException(ex);
            }
            
            for (int i = 0; i < OnSerializedCallbacks.Length; i++) {
                try {
                    OnSerializedCallbacks[i](value, context.StreamingContext);
                } catch (Exception ex) {
                    context.Config.DebugContext.LogException(ex);
                }
            }
        }
        
        public object Deserialize(IDataReader reader) {
            DeserializationContext context = reader.Context;
            object value = GetUninitializedObject();
            
            if (IsValueType) {
                if (ReferenceEquals(null, value)) {
                    value = Activator.CreateInstance(SerializedType);
                }
                
                InvokeOnDeserializingCallbacks(value, context);
            } else {
                if (ReferenceEquals(value, null) == false) {
                    RegisterReferenceID(value, reader);
                    InvokeOnDeserializingCallbacks(value, context);
                    
                    if (ImplementsIObjectReference) {
                        try {
                            value = (value as IObjectReference).GetRealObject(context.StreamingContext);
                            RegisterReferenceID(value, reader);
                        } catch (Exception ex) {
                            context.Config.DebugContext.LogException(ex);
                        }
                    }
                }
            }
            
            try {
                DeserializeImplementation(ref value, reader);
            } catch (Exception ex) {
                context.Config.DebugContext.LogException(ex);
            }
            
            if (IsValueType || ReferenceEquals(value, null) == false) {
                for (int i = 0; i < OnDeserializedCallbacks.Length; i++) {
                    try {
                        OnDeserializedCallbacks[i](value, context.StreamingContext);
                    } catch (Exception ex) {
                        context.Config.DebugContext.LogException(ex);
                    }
                }
                
                if (ImplementsIDeserializationCallback) {
                    IDeserializationCallback v = value as IDeserializationCallback;
                    v.OnDeserialization(this);
                    value = v;
                }
            }
            
            return value;
        }
        
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
        
        protected void InvokeOnDeserializingCallbacks(object value, DeserializationContext context) {
            for (int i = 0; i < OnDeserializingCallbacks.Length; i++) {
                try {
                    OnDeserializingCallbacks[i](value, context.StreamingContext);
                } catch (Exception ex) {
                    context.Config.DebugContext.LogException(ex);
                }
            }
        }
        
        protected virtual object GetUninitializedObject() {
            return IsValueType ? Activator.CreateInstance(SerializedType) : FormatterServices.GetUninitializedObject(SerializedType);
        }
        
        protected abstract void DeserializeImplementation(ref object value, IDataReader reader);
        
        protected abstract void SerializeImplementation(ref object value, IDataWriter writer);
    }
}