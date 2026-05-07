using System;
using System.Collections.Generic;
using System.Reflection;
using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Misc;
using TinySerializer.Core.Serializers;
using TinySerializer.Utilities.Extensions;

namespace TinySerializer.Core.Formatters {
    public class ReflectionFormatter<T> : BaseFormatter<T> {
        public ReflectionFormatter() { }
        
        public ReflectionFormatter(ISerializationPolicy overridePolicy) {
            OverridePolicy = overridePolicy;
        }
        
        public ISerializationPolicy OverridePolicy { get; private set; }
        
        protected override void DeserializeImplementation(ref T value, IDataReader reader) {
            object boxedValue = value;
            
            Dictionary<string, MemberInfo> members = FormatterUtilities.GetSerializableMembersMap(typeof(T), OverridePolicy ?? reader.Context.Config.SerializationPolicy);
            
            EntryType entryType;
            string name;
            
            while ((entryType = reader.PeekEntry(out name)) != EntryType.EndOfNode && entryType != EntryType.EndOfArray && entryType != EntryType.EndOfStream) {
                if (string.IsNullOrEmpty(name)) {
                    reader.Context.Config.DebugContext.LogError("Entry of type \"" + entryType + "\" in node \"" + reader.CurrentNodeName + "\" is missing a name.");
                    reader.SkipEntry();
                    continue;
                }
                
                MemberInfo member;
                
                if (members.TryGetValue(name, out member) == false) {
                    reader.Context.Config.DebugContext.LogWarning("Lost serialization data for entry \"" + name + "\" of type \"" + entryType + "\" in node \""
                                                                  + reader.CurrentNodeName + "\" because a serialized member of that name could not be found in type "
                                                                  + typeof(T).GetNiceFullName() + ".");
                    
                    reader.SkipEntry();
                    continue;
                }
                
                Type expectedType = FormatterUtilities.GetContainedType(member);
                
                try {
                    Serializer serializer = Serializer.Get(expectedType);
                    object entryValue = serializer.ReadValueWeak(reader);
                    FormatterUtilities.SetMemberValue(member, boxedValue, entryValue);
                } catch (Exception ex) {
                    reader.Context.Config.DebugContext.LogException(ex);
                }
            }
            
            value = (T)boxedValue;
        }
        
        protected override void SerializeImplementation(ref T value, IDataWriter writer) {
            MemberInfo[] members = FormatterUtilities.GetSerializableMembers(typeof(T), OverridePolicy ?? writer.Context.Config.SerializationPolicy);
            
            for (int i = 0; i < members.Length; i++) {
                MemberInfo member = members[i];
                Type type;
                object memberValue = FormatterUtilities.GetMemberValue(member, value);
                
                type = FormatterUtilities.GetContainedType(member);
                
                Serializer serializer = Serializer.Get(type);
                
                try {
                    serializer.WriteValueWeak(member.Name, memberValue, writer);
                } catch (Exception ex) {
                    writer.Context.Config.DebugContext.LogException(ex);
                }
            }
        }
    }
    
    public class WeakReflectionFormatter : WeakBaseFormatter {
        public WeakReflectionFormatter(Type serializedType) : base(serializedType) { }
        
        protected override void DeserializeImplementation(ref object value, IDataReader reader) {
            Dictionary<string, MemberInfo> members = FormatterUtilities.GetSerializableMembersMap(SerializedType, reader.Context.Config.SerializationPolicy);
            
            EntryType entryType;
            string name;
            
            while ((entryType = reader.PeekEntry(out name)) != EntryType.EndOfNode && entryType != EntryType.EndOfArray && entryType != EntryType.EndOfStream) {
                if (string.IsNullOrEmpty(name)) {
                    reader.Context.Config.DebugContext.LogError("Entry of type \"" + entryType + "\" in node \"" + reader.CurrentNodeName + "\" is missing a name.");
                    reader.SkipEntry();
                    continue;
                }
                
                MemberInfo member;
                
                if (members.TryGetValue(name, out member) == false) {
                    reader.Context.Config.DebugContext.LogWarning("Lost serialization data for entry \"" + name + "\" of type \"" + entryType + "\" in node \""
                                                                  + reader.CurrentNodeName + "\" because a serialized member of that name could not be found in type "
                                                                  + SerializedType.GetNiceFullName() + ".");
                    
                    reader.SkipEntry();
                    continue;
                }
                
                Type expectedType = FormatterUtilities.GetContainedType(member);
                
                try {
                    Serializer serializer = Serializer.Get(expectedType);
                    object entryValue = serializer.ReadValueWeak(reader);
                    FormatterUtilities.SetMemberValue(member, value, entryValue);
                } catch (Exception ex) {
                    reader.Context.Config.DebugContext.LogException(ex);
                }
            }
        }
        
        protected override void SerializeImplementation(ref object value, IDataWriter writer) {
            MemberInfo[] members = FormatterUtilities.GetSerializableMembers(SerializedType, writer.Context.Config.SerializationPolicy);
            
            for (int i = 0; i < members.Length; i++) {
                MemberInfo member = members[i];
                Type type;
                object memberValue = FormatterUtilities.GetMemberValue(member, value);
                
                type = FormatterUtilities.GetContainedType(member);
                
                Serializer serializer = Serializer.Get(type);
                
                try {
                    serializer.WriteValueWeak(member.Name, memberValue, writer);
                } catch (Exception ex) {
                    writer.Context.Config.DebugContext.LogException(ex);
                }
            }
        }
    }
}