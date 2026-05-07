using System;
using System.Collections.Generic;
using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.FormatterLocators;
using TinySerializer.Core.Formatters;
using TinySerializer.Core.Misc;
using TinySerializer.Utilities.Extensions;
using TinySerializer.Utilities.Misc;

namespace TinySerializer.Core.Serializers {
    public sealed class AnySerializer : Serializer {
        private static readonly ISerializationPolicy UnityPolicy = SerializationPolicies.Unity;
        private static readonly ISerializationPolicy StrictPolicy = SerializationPolicies.Strict;
        private static readonly ISerializationPolicy EverythingPolicy = SerializationPolicies.Everything;
        
        private readonly Type SerializedType;
        private readonly bool IsEnum;
        private readonly bool IsValueType;
        private readonly bool MayBeBoxedValueType;
        private readonly bool IsAbstract;
        private readonly bool IsNullable;
        
        private readonly bool AllowDeserializeInvalidData;
        
        private IFormatter UnityPolicyFormatter;
        private IFormatter StrictPolicyFormatter;
        private IFormatter EverythingPolicyFormatter;
        
        private readonly Dictionary<ISerializationPolicy, IFormatter> FormattersByPolicy =
            new Dictionary<ISerializationPolicy, IFormatter>(ReferenceEqualityComparer<ISerializationPolicy>.Default);
        
        private readonly object FormattersByPolicy_LOCK = new object();
        
        public AnySerializer(Type serializedType) {
            SerializedType = serializedType;
            IsEnum = SerializedType.IsEnum;
            IsValueType = SerializedType.IsValueType;
            
            MayBeBoxedValueType = SerializedType.IsInterface || SerializedType == typeof(object) || SerializedType == typeof(ValueType)
                || SerializedType == typeof(Enum);
            
            IsAbstract = SerializedType.IsAbstract || SerializedType.IsInterface;
            IsNullable = SerializedType.IsGenericType && SerializedType.GetGenericTypeDefinition() == typeof(Nullable<>);
            AllowDeserializeInvalidData = SerializedType.IsDefined(typeof(AllowDeserializeInvalidDataAttribute), true);
        }
        
        public override object ReadValueWeak(IDataReader reader) {
            if (IsEnum) {
                string name;
                EntryType entry = reader.PeekEntry(out name);
                
                if (entry == EntryType.Integer) {
                    ulong value;
                    
                    if (reader.ReadUInt64(out value) == false) {
                        reader.Context.Config.DebugContext.LogWarning("Failed to read entry '" + name + "' of type " + entry.ToString());
                    }
                    
                    return Enum.ToObject(SerializedType, value);
                } else {
                    reader.Context.Config.DebugContext.LogWarning("Expected entry of type " + EntryType.Integer.ToString() + ", but got entry '" + name + "' of type "
                                                                  + entry.ToString());
                    
                    reader.SkipEntry();
                    return Activator.CreateInstance(SerializedType);
                }
            } else {
                DeserializationContext context = reader.Context;
                
                if (context.Config.SerializationPolicy.AllowNonSerializableTypes == false && SerializedType.IsSerializable == false) {
                    context.Config.DebugContext.LogError("The type " + SerializedType.Name + " is not marked as serializable.");
                    return IsValueType ? Activator.CreateInstance(SerializedType) : null;
                }
                
                bool exitNode = true;
                
                string name;
                EntryType entry = reader.PeekEntry(out name);
                
                if (IsValueType) {
                    if (entry == EntryType.Null) {
                        context.Config.DebugContext.LogWarning("Expecting complex struct of type " + SerializedType.GetNiceFullName() + " but got null value.");
                        reader.ReadNull();
                        return Activator.CreateInstance(SerializedType);
                    } else if (entry != EntryType.StartOfNode) {
                        context.Config.DebugContext.LogWarning("Unexpected entry '" + name + "' of type " + entry.ToString() + ", when " + EntryType.StartOfNode
                                                               + " was expected. A value has likely been lost.");
                        
                        reader.SkipEntry();
                        return Activator.CreateInstance(SerializedType);
                    }
                    
                    try {
                        Type expectedType = SerializedType;
                        Type serializedType;
                        
                        if (reader.EnterNode(out serializedType)) {
                            if (serializedType != expectedType) {
                                if (serializedType != null) {
                                    context.Config.DebugContext.LogWarning("Expected complex struct value " + expectedType.Name + " but the serialized value is of type "
                                                                           + serializedType.Name + ".");
                                    
                                    if (serializedType.IsCastableTo(expectedType)) {
                                        object value = FormatterLocator.GetFormatter(serializedType, context.Config.SerializationPolicy).Deserialize(reader);
                                        
                                        bool serializedTypeIsNullable = serializedType.IsGenericType && serializedType.GetGenericTypeDefinition() == typeof(Nullable<>);
                                        bool allowCastMethod = !IsNullable && !serializedTypeIsNullable;
                                        
                                        Func<object, object> castMethod = allowCastMethod ? serializedType.GetCastMethodDelegate(expectedType) : null;
                                        
                                        if (castMethod != null) {
                                            return castMethod(value);
                                        } else {
                                            return value;
                                        }
                                    } else if (AllowDeserializeInvalidData || reader.Context.Config.AllowDeserializeInvalidData) {
                                        context.Config.DebugContext.LogWarning("Can't cast serialized type " + serializedType.GetNiceFullName() + " into expected type "
                                                                               + expectedType.GetNiceFullName()
                                                                               + ". Attempting to deserialize with possibly invalid data. Value may be lost or corrupted for node '"
                                                                               + name + "'.");
                                        
                                        return GetBaseFormatter(context.Config.SerializationPolicy).Deserialize(reader);
                                    } else {
                                        context.Config.DebugContext.LogWarning("Can't cast serialized type " + serializedType.GetNiceFullName() + " into expected type "
                                                                               + expectedType.GetNiceFullName() + ". Value lost for node '" + name + "'.");
                                        
                                        return Activator.CreateInstance(SerializedType);
                                    }
                                } else if (AllowDeserializeInvalidData || reader.Context.Config.AllowDeserializeInvalidData) {
                                    context.Config.DebugContext.LogWarning("Expected complex struct value " + expectedType.GetNiceFullName()
                                                                           + " but the serialized type could not be resolved. Attempting to deserialize with possibly invalid data. Value may be lost or corrupted for node '"
                                                                           + name + "'.");
                                    
                                    return GetBaseFormatter(context.Config.SerializationPolicy).Deserialize(reader);
                                } else {
                                    context.Config.DebugContext.LogWarning("Expected complex struct value " + expectedType.Name
                                                                           + " but the serialized type could not be resolved. Value lost for node '" + name + "'.");
                                    
                                    return Activator.CreateInstance(SerializedType);
                                }
                            } else {
                                return GetBaseFormatter(context.Config.SerializationPolicy).Deserialize(reader);
                            }
                        } else {
                            context.Config.DebugContext.LogError("Failed to enter node '" + name + "'.");
                            return Activator.CreateInstance(SerializedType);
                        }
                    } catch (SerializationAbortException ex) {
                        exitNode = false;
                        throw ex;
                    } catch (Exception ex) {
                        context.Config.DebugContext.LogException(ex);
                        return Activator.CreateInstance(SerializedType);
                    } finally {
                        if (exitNode) {
                            reader.ExitNode();
                        }
                    }
                } else {
                    switch (entry) {
                        case EntryType.Null: {
                            reader.ReadNull();
                            return null;
                        }
                        
                        case EntryType.ExternalReferenceByIndex: {
                            int index;
                            reader.ReadExternalReference(out index);
                            
                            object value = context.GetExternalObject(index);
                            
                            if (!ReferenceEquals(value, null) && !SerializedType.IsAssignableFrom(value.GetType())) {
                                context.Config.DebugContext.LogWarning("Can't cast external reference type " + value.GetType().GetNiceFullName() + " into expected type "
                                                                       + SerializedType.GetNiceFullName() + ". Value lost for node '" + name + "'.");
                                
                                return null;
                            }
                            
                            return value;
                        }
                        
                        case EntryType.ExternalReferenceByGuid: {
                            Guid guid;
                            reader.ReadExternalReference(out guid);
                            
                            object value = context.GetExternalObject(guid);
                            
                            if (!ReferenceEquals(value, null) && !SerializedType.IsAssignableFrom(value.GetType())) {
                                context.Config.DebugContext.LogWarning("Can't cast external reference type " + value.GetType().GetNiceFullName() + " into expected type "
                                                                       + SerializedType.GetNiceFullName() + ". Value lost for node '" + name + "'.");
                                
                                return null;
                            }
                            
                            return value;
                        }
                        
                        case EntryType.ExternalReferenceByString: {
                            string id;
                            reader.ReadExternalReference(out id);
                            
                            object value = context.GetExternalObject(id);
                            
                            if (!ReferenceEquals(value, null) && !SerializedType.IsAssignableFrom(value.GetType())) {
                                context.Config.DebugContext.LogWarning("Can't cast external reference type " + value.GetType().GetNiceFullName() + " into expected type "
                                                                       + SerializedType.GetNiceFullName() + ". Value lost for node '" + name + "'.");
                                
                                return null;
                            }
                            
                            return value;
                        }
                        
                        case EntryType.InternalReference: {
                            int id;
                            reader.ReadInternalReference(out id);
                            
                            object value = context.GetInternalReference(id);
                            
                            if (!ReferenceEquals(value, null) && !SerializedType.IsAssignableFrom(value.GetType())) {
                                context.Config.DebugContext.LogWarning("Can't cast internal reference type " + value.GetType().GetNiceFullName() + " into expected type "
                                                                       + SerializedType.GetNiceFullName() + ". Value lost for node '" + name + "'.");
                                
                                return null;
                            }
                            
                            return value;
                        }
                        
                        case EntryType.StartOfNode: {
                            try {
                                Type expectedType = SerializedType;
                                Type serializedType;
                                int id;
                                
                                if (reader.EnterNode(out serializedType)) {
                                    id = reader.CurrentNodeId;
                                    
                                    object result;
                                    
                                    if (serializedType != null && expectedType != serializedType) {
                                        bool success = false;
                                        bool isPrimitive = FormatterUtilities.IsPrimitiveType(serializedType);
                                        
                                        bool assignableCast;
                                        
                                        if (MayBeBoxedValueType && isPrimitive) {
                                            Serializer serializer = Get(serializedType);
                                            result = serializer.ReadValueWeak(reader);
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
                                                    result = value;
                                                } else {
                                                    Func<object, object> castMethod = serializedType.GetCastMethodDelegate(expectedType);
                                                    
                                                    if (castMethod != null) {
                                                        result = castMethod(value);
                                                    } else {
                                                        result = value;
                                                    }
                                                }
                                                
                                                success = true;
                                            } catch (SerializationAbortException ex) {
                                                exitNode = false;
                                                throw ex;
                                            } catch (InvalidCastException) {
                                                success = false;
                                                result = null;
                                            }
                                        } else if (!IsAbstract && (AllowDeserializeInvalidData || reader.Context.Config.AllowDeserializeInvalidData)) {
                                            context.Config.DebugContext.LogWarning("Can't cast serialized type " + serializedType.GetNiceFullName() + " into expected type "
                                                                                   + expectedType.GetNiceFullName()
                                                                                   + ". Attempting to deserialize with invalid data. Value may be lost or corrupted for node '"
                                                                                   + name + "'.");
                                            
                                            result = GetBaseFormatter(context.Config.SerializationPolicy).Deserialize(reader);
                                            success = true;
                                        } else {
                                            
                                            IFormatter alternateFormatter = FormatterLocator.GetFormatter(serializedType, context.Config.SerializationPolicy);
                                            object value = alternateFormatter.Deserialize(reader);
                                            
                                            if (id >= 0) {
                                                context.RegisterInternalReference(id, value);
                                            }
                                            
                                            result = null;
                                        }
                                        
                                        if (!success) {
                                            context.Config.DebugContext.LogWarning("Can't cast serialized type " + serializedType.GetNiceFullName() + " into expected type "
                                                                                   + expectedType.GetNiceFullName() + ". Value lost for node '" + name + "'.");
                                            
                                            result = null;
                                        }
                                    } else if (IsAbstract) {
                                        result = null;
                                    } else {
                                        result = GetBaseFormatter(context.Config.SerializationPolicy).Deserialize(reader);
                                    }
                                    
                                    if (id >= 0) {
                                        context.RegisterInternalReference(id, result);
                                    }
                                    
                                    return result;
                                } else {
                                    context.Config.DebugContext.LogError("Failed to enter node '" + name + "'.");
                                    return null;
                                }
                            } catch (SerializationAbortException ex) {
                                exitNode = false;
                                throw ex;
                            } catch (Exception ex) {
                                context.Config.DebugContext.LogException(ex);
                                return null;
                            } finally {
                                if (exitNode) {
                                    reader.ExitNode();
                                }
                            }
                        }
                        
                        case EntryType.Boolean: {
                            if (!MayBeBoxedValueType) {
                                goto default;
                            }
                            
                            bool value;
                            reader.ReadBoolean(out value);
                            return value;
                        }
                        
                        case EntryType.FloatingPoint: {
                            if (!MayBeBoxedValueType) {
                                goto default;
                            }
                            
                            double value;
                            reader.ReadDouble(out value);
                            return value;
                        }
                        
                        case EntryType.Integer: {
                            if (!MayBeBoxedValueType) {
                                goto default;
                            }
                            
                            long value;
                            reader.ReadInt64(out value);
                            return value;
                        }
                        
                        case EntryType.String: {
                            if (!MayBeBoxedValueType) {
                                goto default;
                            }
                            
                            string value;
                            reader.ReadString(out value);
                            return value;
                        }
                        
                        case EntryType.Guid: {
                            if (!MayBeBoxedValueType) {
                                goto default;
                            }
                            
                            Guid value;
                            reader.ReadGuid(out value);
                            return value;
                        }
                        
                        default:
                            
                            context.Config.DebugContext.LogWarning("Unexpected entry of type " + entry.ToString()
                                                                   + ", when a reference or node start was expected. A value has been lost.");
                            
                            reader.SkipEntry();
                            return null;
                    }
                }
            }
        }
        
        public override void WriteValueWeak(string name, object value, IDataWriter writer) {
            if (IsEnum) {
                ulong ul;
                
                FireOnSerializedType(SerializedType);
                
                try {
                    ul = Convert.ToUInt64(value as Enum);
                } catch (OverflowException) {
                    unchecked {
                        ul = (ulong)Convert.ToInt64(value as Enum);
                    }
                }
                
                writer.WriteUInt64(name, ul);
            } else {
                SerializationContext context = writer.Context;
                ISerializationPolicy policy = context.Config.SerializationPolicy;
                
                if (policy.AllowNonSerializableTypes == false && SerializedType.IsSerializable == false) {
                    context.Config.DebugContext.LogError("The type " + SerializedType.Name + " is not marked as serializable.");
                    return;
                }
                
                FireOnSerializedType(SerializedType);
                
                if (IsValueType) {
                    bool endNode = true;
                    
                    try {
                        writer.BeginStructNode(name, SerializedType);
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
                        
                        if (MayBeBoxedValueType && FormatterUtilities.IsPrimitiveType(type)) {
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
                            
                            if (ReferenceEquals(type, SerializedType)) {
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
        
        private IFormatter GetBaseFormatter(ISerializationPolicy serializationPolicy) {
            if (ReferenceEquals(serializationPolicy, UnityPolicy)) {
                if (UnityPolicyFormatter == null) {
                    UnityPolicyFormatter = FormatterLocator.GetFormatter(SerializedType, UnityPolicy);
                }
                
                return UnityPolicyFormatter;
            } else if (ReferenceEquals(serializationPolicy, EverythingPolicy)) {
                if (EverythingPolicyFormatter == null) {
                    EverythingPolicyFormatter = FormatterLocator.GetFormatter(SerializedType, EverythingPolicy);
                }
                
                return EverythingPolicyFormatter;
            } else if (ReferenceEquals(serializationPolicy, StrictPolicy)) {
                if (StrictPolicyFormatter == null) {
                    StrictPolicyFormatter = FormatterLocator.GetFormatter(SerializedType, StrictPolicy);
                }
                
                return StrictPolicyFormatter;
            }
            
            IFormatter formatter;
            
            lock (FormattersByPolicy_LOCK) {
                if (!FormattersByPolicy.TryGetValue(serializationPolicy, out formatter)) {
                    formatter = FormatterLocator.GetFormatter(SerializedType, serializationPolicy);
                    FormattersByPolicy.Add(serializationPolicy, formatter);
                }
            }
            
            return formatter;
        }
    }
}