using System;
using System.Collections.Generic;
using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.FormatterLocators;
using TinySerializer.Core.Formatters;
using TinySerializer.Core.Misc;
using TinySerializer.Utilities.Extensions;
using TinySerializer.Utilities.Misc;

namespace TinySerializer.Core.Serializers {
    public class ComplexTypeSerializer<T> : Serializer<T> {
        private static readonly bool ComplexTypeMayBeBoxedValueType =
            typeof(T).IsInterface || typeof(T) == typeof(object) || typeof(T) == typeof(ValueType) || typeof(T) == typeof(Enum);
        
        private static readonly bool ComplexTypeIsAbstract = typeof(T).IsAbstract || typeof(T).IsInterface;
        private static readonly bool ComplexTypeIsNullable = typeof(T).IsGenericType && typeof(T).GetGenericTypeDefinition() == typeof(Nullable<>);
        private static readonly bool ComplexTypeIsValueType = typeof(T).IsValueType;
        private static readonly Type TypeOf_T = typeof(T);
        
        private static readonly bool AllowDeserializeInvalidDataForT = typeof(T).IsDefined(typeof(AllowDeserializeInvalidDataAttribute), true);
        
        private static readonly Dictionary<ISerializationPolicy, IFormatter<T>> FormattersByPolicy =
            new Dictionary<ISerializationPolicy, IFormatter<T>>(ReferenceEqualityComparer<ISerializationPolicy>.Default);
        
        private static readonly object FormattersByPolicy_LOCK = new object();
        
        private static readonly ISerializationPolicy UnityPolicy = SerializationPolicies.Unity;
        private static readonly ISerializationPolicy StrictPolicy = SerializationPolicies.Strict;
        private static readonly ISerializationPolicy EverythingPolicy = SerializationPolicies.Everything;
        
        private static IFormatter<T> UnityPolicyFormatter;
        private static IFormatter<T> StrictPolicyFormatter;
        private static IFormatter<T> EverythingPolicyFormatter;
        
        public override T ReadValue(IDataReader reader) {
            DeserializationContext context = reader.Context;
            
            if (context.Config.SerializationPolicy.AllowNonSerializableTypes == false && TypeOf_T.IsSerializable == false) {
                context.Config.DebugContext.LogError("The type " + TypeOf_T.GetNiceFullName() + " is not marked as serializable.");
                return default(T);
            }
            
            bool exitNode = true;
            
            string name;
            EntryType entry = reader.PeekEntry(out name);
            
            if (ComplexTypeIsValueType) {
                if (entry == EntryType.Null) {
                    context.Config.DebugContext.LogWarning("Expecting complex struct of type " + TypeOf_T.GetNiceFullName() + " but got null value.");
                    reader.ReadNull();
                    return default(T);
                } else if (entry != EntryType.StartOfNode) {
                    context.Config.DebugContext.LogWarning("Unexpected entry '" + name + "' of type " + entry.ToString() + ", when " + EntryType.StartOfNode
                                                           + " was expected. A value has likely been lost.");
                    
                    reader.SkipEntry();
                    return default(T);
                }
                
                try {
                    Type expectedType = TypeOf_T;
                    Type serializedType;
                    
                    if (reader.EnterNode(out serializedType)) {
                        if (serializedType != expectedType) {
                            if (serializedType != null) {
                                context.Config.DebugContext.LogWarning("Expected complex struct value " + expectedType.GetNiceFullName() + " but the serialized value is of type "
                                                                       + serializedType.GetNiceFullName() + ".");
                                
                                if (serializedType.IsCastableTo(expectedType)) {
                                    object value = FormatterLocator.GetFormatter(serializedType, context.Config.SerializationPolicy).Deserialize(reader);
                                    
                                    bool serializedTypeIsNullable = serializedType.IsGenericType && serializedType.GetGenericTypeDefinition() == typeof(Nullable<>);
                                    bool allowCastMethod = !ComplexTypeIsNullable && !serializedTypeIsNullable;
                                    
                                    Func<object, object> castMethod = allowCastMethod ? serializedType.GetCastMethodDelegate(expectedType) : null;
                                    
                                    if (castMethod != null) {
                                        return (T)castMethod(value);
                                    } else {
                                        return (T)value;
                                    }
                                } else if (AllowDeserializeInvalidDataForT || reader.Context.Config.AllowDeserializeInvalidData) {
                                    context.Config.DebugContext.LogWarning("Can't cast serialized type " + serializedType.GetNiceFullName() + " into expected type "
                                                                           + expectedType.GetNiceFullName()
                                                                           + ". Attempting to deserialize with possibly invalid data. Value may be lost or corrupted for node '"
                                                                           + name + "'.");
                                    
                                    return GetBaseFormatter(context.Config.SerializationPolicy).Deserialize(reader);
                                } else {
                                    context.Config.DebugContext.LogWarning("Can't cast serialized type " + serializedType.GetNiceFullName() + " into expected type "
                                                                           + expectedType.GetNiceFullName() + ". Value lost for node '" + name + "'.");
                                    
                                    return default(T);
                                }
                            } else if (AllowDeserializeInvalidDataForT || reader.Context.Config.AllowDeserializeInvalidData) {
                                context.Config.DebugContext.LogWarning("Expected complex struct value " + expectedType.GetNiceFullName()
                                                                       + " but the serialized type could not be resolved. Attempting to deserialize with possibly invalid data. Value may be lost or corrupted for node '"
                                                                       + name + "'.");
                                
                                return GetBaseFormatter(context.Config.SerializationPolicy).Deserialize(reader);
                            } else {
                                context.Config.DebugContext.LogWarning("Expected complex struct value " + expectedType.GetNiceFullName()
                                                                       + " but the serialized type could not be resolved. Value lost for node '" + name + "'.");
                                
                                return default(T);
                            }
                        } else {
                            return GetBaseFormatter(context.Config.SerializationPolicy).Deserialize(reader);
                        }
                    } else {
                        context.Config.DebugContext.LogError("Failed to enter node '" + name + "'.");
                        return default(T);
                    }
                } catch (SerializationAbortException ex) {
                    exitNode = false;
                    throw ex;
                } catch (Exception ex) {
                    context.Config.DebugContext.LogException(ex);
                    return default(T);
                } finally {
                    if (exitNode) {
                        reader.ExitNode();
                    }
                }
            } else {
                switch (entry) {
                    case EntryType.Null: {
                        reader.ReadNull();
                        return default(T);
                    }
                    
                    case EntryType.ExternalReferenceByIndex: {
                        int index;
                        reader.ReadExternalReference(out index);
                        
                        object value = context.GetExternalObject(index);
                        
                        try {
                            return (T)value;
                        } catch (InvalidCastException) {
                            context.Config.DebugContext.LogWarning("Can't cast external reference type " + value.GetType().GetNiceFullName() + " into expected type "
                                                                   + TypeOf_T.GetNiceFullName() + ". Value lost for node '" + name + "'.");
                            
                            return default(T);
                        }
                    }
                    
                    case EntryType.ExternalReferenceByGuid: {
                        Guid guid;
                        reader.ReadExternalReference(out guid);
                        
                        object value = context.GetExternalObject(guid);
                        
                        try {
                            return (T)value;
                        } catch (InvalidCastException) {
                            context.Config.DebugContext.LogWarning("Can't cast external reference type " + value.GetType().GetNiceFullName() + " into expected type "
                                                                   + TypeOf_T.GetNiceFullName() + ". Value lost for node '" + name + "'.");
                            
                            return default(T);
                        }
                    }
                    
                    case EntryType.ExternalReferenceByString: {
                        string id;
                        reader.ReadExternalReference(out id);
                        
                        object value = context.GetExternalObject(id);
                        
                        try {
                            return (T)value;
                        } catch (InvalidCastException) {
                            context.Config.DebugContext.LogWarning("Can't cast external reference type " + value.GetType().GetNiceFullName() + " into expected type "
                                                                   + TypeOf_T.GetNiceFullName() + ". Value lost for node '" + name + "'.");
                            
                            return default(T);
                        }
                    }
                    
                    case EntryType.InternalReference: {
                        int id;
                        reader.ReadInternalReference(out id);
                        
                        object value = context.GetInternalReference(id);
                        
                        try {
                            return (T)value;
                        } catch (InvalidCastException) {
                            context.Config.DebugContext.LogWarning("Can't cast internal reference type " + value.GetType().GetNiceFullName() + " into expected type "
                                                                   + TypeOf_T.GetNiceFullName() + ". Value lost for node '" + name + "'.");
                            
                            return default(T);
                        }
                    }
                    
                    case EntryType.StartOfNode: {
                        try {
                            Type expectedType = TypeOf_T;
                            Type serializedType;
                            int id;
                            
                            if (reader.EnterNode(out serializedType)) {
                                id = reader.CurrentNodeId;
                                
                                T result;
                                
                                if (serializedType != null && expectedType != serializedType) {
                                    bool success = false;
                                    bool isPrimitive = FormatterUtilities.IsPrimitiveType(serializedType);
                                    
                                    bool assignableCast;
                                    
                                    if (ComplexTypeMayBeBoxedValueType && isPrimitive) {
                                        Serializer serializer = Get(serializedType);
                                        result = (T)serializer.ReadValueWeak(reader);
                                        success = true;
                                    } else if ((assignableCast = expectedType.IsAssignableFrom(serializedType)) || serializedType.HasCastDefined(expectedType, false)) {
                                        try {
                                            object value;
                                            
                                            if (isPrimitive) {
                                                Serializer serializer = Get(serializedType);
                                                value = serializer.ReadValueWeak(reader);
                                            } else {
                                                IFormatter alternateFormatter = FormatterLocator.GetFormatter(serializedType, context.Config.SerializationPolicy);
                                                value = alternateFormatter.Deserialize(reader);
                                            }
                                            
                                            if (assignableCast) {
                                                result = (T)value;
                                            } else {
                                                Func<object, object> castMethod = serializedType.GetCastMethodDelegate(expectedType);
                                                
                                                if (castMethod != null) {
                                                    result = (T)castMethod(value);
                                                } else {
                                                    result = (T)value;
                                                }
                                            }
                                            
                                            success = true;
                                        } catch (SerializationAbortException ex) {
                                            exitNode = false;
                                            throw ex;
                                        } catch (InvalidCastException) {
                                            success = false;
                                            result = default(T);
                                        }
                                    } else if (!ComplexTypeIsAbstract && (AllowDeserializeInvalidDataForT || reader.Context.Config.AllowDeserializeInvalidData)) {
                                        context.Config.DebugContext.LogWarning("Can't cast serialized type " + serializedType.GetNiceFullName() + " into expected type "
                                                                               + expectedType.GetNiceFullName()
                                                                               + ". Attempting to deserialize with invalid data. Value may be lost or corrupted for node '" + name
                                                                               + "'.");
                                        
                                        result = GetBaseFormatter(context.Config.SerializationPolicy).Deserialize(reader);
                                        success = true;
                                    } else {
                                        
                                        IFormatter alternateFormatter = FormatterLocator.GetFormatter(serializedType, context.Config.SerializationPolicy);
                                        object value = alternateFormatter.Deserialize(reader);
                                        
                                        if (id >= 0) {
                                            context.RegisterInternalReference(id, value);
                                        }
                                        
                                        result = default(T);
                                    }
                                    
                                    if (!success) {
                                        context.Config.DebugContext.LogWarning("Can't cast serialized type " + serializedType.GetNiceFullName() + " into expected type "
                                                                               + expectedType.GetNiceFullName() + ". Value lost for node '" + name + "'.");
                                        
                                        result = default(T);
                                    }
                                } else if (ComplexTypeIsAbstract) {
                                    result = default(T);
                                } else {
                                    result = GetBaseFormatter(context.Config.SerializationPolicy).Deserialize(reader);
                                }
                                
                                if (id >= 0) {
                                    context.RegisterInternalReference(id, result);
                                }
                                
                                return result;
                            } else {
                                context.Config.DebugContext.LogError("Failed to enter node '" + name + "'.");
                                return default(T);
                            }
                        } catch (SerializationAbortException ex) {
                            exitNode = false;
                            throw ex;
                        } catch (Exception ex) {
                            context.Config.DebugContext.LogException(ex);
                            return default(T);
                        } finally {
                            if (exitNode) {
                                reader.ExitNode();
                            }
                        }
                    }
                    
                    case EntryType.Boolean: {
                        if (!ComplexTypeMayBeBoxedValueType) {
                            goto default;
                        }
                        
                        bool value;
                        reader.ReadBoolean(out value);
                        return (T)(object)value;
                    }
                    
                    case EntryType.FloatingPoint: {
                        if (!ComplexTypeMayBeBoxedValueType) {
                            goto default;
                        }
                        
                        double value;
                        reader.ReadDouble(out value);
                        return (T)(object)value;
                    }
                    
                    case EntryType.Integer: {
                        if (!ComplexTypeMayBeBoxedValueType) {
                            goto default;
                        }
                        
                        long value;
                        reader.ReadInt64(out value);
                        return (T)(object)value;
                    }
                    
                    case EntryType.String: {
                        if (!ComplexTypeMayBeBoxedValueType) {
                            goto default;
                        }
                        
                        string value;
                        reader.ReadString(out value);
                        return (T)(object)value;
                    }
                    
                    case EntryType.Guid: {
                        if (!ComplexTypeMayBeBoxedValueType) {
                            goto default;
                        }
                        
                        Guid value;
                        reader.ReadGuid(out value);
                        return (T)(object)value;
                    }
                    
                    default:
                        
                        context.Config.DebugContext.LogWarning("Unexpected entry of type " + entry.ToString()
                                                               + ", when a reference or node start was expected. A value has been lost.");
                        
                        reader.SkipEntry();
                        return default(T);
                }
            }
        }
        
        private static IFormatter<T> GetBaseFormatter(ISerializationPolicy serializationPolicy) {
            
            if (ReferenceEquals(serializationPolicy, UnityPolicy)) {
                if (UnityPolicyFormatter == null) {
                    UnityPolicyFormatter = FormatterLocator.GetFormatter<T>(UnityPolicy);
                }
                
                return UnityPolicyFormatter;
            } else if (ReferenceEquals(serializationPolicy, EverythingPolicy)) {
                if (EverythingPolicyFormatter == null) {
                    EverythingPolicyFormatter = FormatterLocator.GetFormatter<T>(EverythingPolicy);
                }
                
                return EverythingPolicyFormatter;
            } else if (ReferenceEquals(serializationPolicy, StrictPolicy)) {
                if (StrictPolicyFormatter == null) {
                    StrictPolicyFormatter = FormatterLocator.GetFormatter<T>(StrictPolicy);
                }
                
                return StrictPolicyFormatter;
            }
            
            IFormatter<T> formatter;
            
            lock (FormattersByPolicy_LOCK) {
                if (!FormattersByPolicy.TryGetValue(serializationPolicy, out formatter)) {
                    formatter = FormatterLocator.GetFormatter<T>(serializationPolicy);
                    FormattersByPolicy.Add(serializationPolicy, formatter);
                }
            }
            
            return formatter;
        }
        
        /// <summary>
        /// Writes a value of type <see cref="T" />.
        /// </summary>
        /// <param name="name">The name of the value to write.</param>
        /// <param name="value">The value to write.</param>
        /// <param name="writer">The writer to use.</param>
        public override void WriteValue(string name, T value, IDataWriter writer) {
            SerializationContext context = writer.Context;
            ISerializationPolicy policy = context.Config.SerializationPolicy;
            
            if (policy.AllowNonSerializableTypes == false && TypeOf_T.IsSerializable == false) {
                context.Config.DebugContext.LogError("The type " + TypeOf_T.GetNiceFullName() + " is not marked as serializable.");
                return;
            }
            
            FireOnSerializedType();
            
            if (ComplexTypeIsValueType) {
                bool endNode = true;
                
                try {
                    writer.BeginStructNode(name, TypeOf_T);
                    GetBaseFormatter(policy).Serialize(value, writer);
                } catch (SerializationAbortException ex) {
                    endNode = false;
                    throw ex;
                } finally {
                    if (endNode) {
                        writer.EndNode(name);
                    }
                }
            } else {
                int id;
                int index;
                string strId;
                Guid guid;
                
                bool endNode = true;
                
                if (ReferenceEquals(value, null)) {
                    writer.WriteNull(name);
                } else if (context.TryRegisterExternalReference(value, out index)) {
                    writer.WriteExternalReference(name, index);
                } else if (context.TryRegisterExternalReference(value, out guid)) {
                    writer.WriteExternalReference(name, guid);
                } else if (context.TryRegisterExternalReference(value, out strId)) {
                    writer.WriteExternalReference(name, strId);
                } else if (context.TryRegisterInternalReference(value, out id)) {
                    
                    Type type = (value as object).GetType();
                    
                    if (ComplexTypeMayBeBoxedValueType && FormatterUtilities.IsPrimitiveType(type)) {
                        try {
                            writer.BeginReferenceNode(name, type, id);
                            
                            Serializer serializer = Get(type);
                            serializer.WriteValueWeak(value, writer);
                        } catch (SerializationAbortException ex) {
                            endNode = false;
                            throw ex;
                        } finally {
                            if (endNode) {
                                writer.EndNode(name);
                            }
                        }
                    } else {
                        IFormatter formatter;
                        
                        if (ReferenceEquals(type, TypeOf_T)) {
                            formatter = GetBaseFormatter(policy);
                        } else {
                            formatter = FormatterLocator.GetFormatter(type, policy);
                        }
                        
                        try {
                            writer.BeginReferenceNode(name, type, id);
                            formatter.Serialize(value, writer);
                        } catch (SerializationAbortException ex) {
                            endNode = false;
                            throw ex;
                        } finally {
                            if (endNode) {
                                writer.EndNode(name);
                            }
                        }
                    }
                } else {
                    writer.WriteInternalReference(name, id);
                }
            }
        }
    }
}