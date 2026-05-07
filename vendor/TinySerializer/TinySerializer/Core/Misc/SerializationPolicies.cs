using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using TinySerializer.Utilities;
using TinySerializer.Utilities.Extensions;

namespace TinySerializer.Core.Misc {
    public static class SerializationPolicies {
        private static readonly object LOCK = new object();
        
        private static volatile ISerializationPolicy everythingPolicy;
        private static volatile ISerializationPolicy unityPolicy;
        private static volatile ISerializationPolicy strictPolicy;
        
        public static ISerializationPolicy Everything {
            get {
                if (everythingPolicy == null) {
                    lock (LOCK) {
                        if (everythingPolicy == null) {
                            everythingPolicy = new CustomSerializationPolicy("OdinSerializerPolicies.Everything", true, (member) =>
                            {
                                if (!(member is FieldInfo)) {
                                    return false;
                                }
                                
                                return !member.IsDefined<NonSerializedAttribute>(true);
                            });
                        }
                    }
                }
                
                return everythingPolicy;
            }
        }
        
        public static ISerializationPolicy Unity {
            get {
                if (unityPolicy == null) {
                    lock (LOCK) {
                        if (unityPolicy == null) {
                            Type tupleInterface = typeof(string).Assembly.GetType("System.ITuple") ?? typeof(string).Assembly.GetType("System.ITupleInternal");
                            
                            unityPolicy = new CustomSerializationPolicy("OdinSerializerPolicies.Unity", true, (member) =>
                            {
                                if (member is PropertyInfo) {
                                    PropertyInfo propInfo = member as PropertyInfo;
                                    if (propInfo.GetGetMethod(true) == null || propInfo.GetSetMethod(true) == null) return false;
                                }
                                
                                if (member.IsDefined<NonSerializedAttribute>(true)) {
                                    return false;
                                }
                                
                                if (member is FieldInfo && ((member as FieldInfo).IsPublic
                                    || (member.DeclaringType.IsNestedPrivate && member.DeclaringType.IsDefined<CompilerGeneratedAttribute>())
                                    || (tupleInterface != null && tupleInterface.IsAssignableFrom(member.DeclaringType)))) {
                                    return true;
                                }
                                
                                return false;
                            });
                        }
                    }
                }
                
                return unityPolicy;
            }
        }
        
        public static ISerializationPolicy Strict {
            get {
                if (strictPolicy == null) {
                    lock (LOCK) {
                        if (strictPolicy == null) {
                            strictPolicy = new CustomSerializationPolicy("OdinSerializerPolicies.Strict", true, (member) =>
                            {
                                if (member is PropertyInfo && ((PropertyInfo)member).IsAutoProperty() == false) {
                                    return false;
                                }
                                
                                if (member.IsDefined<NonSerializedAttribute>()) {
                                    return false;
                                }
                                
                                if (member is FieldInfo && member.DeclaringType.IsNestedPrivate && member.DeclaringType.IsDefined<CompilerGeneratedAttribute>()) {
                                    return true;
                                }
                                
                                return false;
                            });
                        }
                    }
                }
                
                return strictPolicy;
            }
        }
    }
}