using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TinySerializer.Utilities.Misc;

namespace TinySerializer.Core.Misc {
    public static class FormatterUtilities {
        private static readonly DoubleLookupDictionary<ISerializationPolicy, Type, MemberInfo[]> MemberArrayCache =
            new DoubleLookupDictionary<ISerializationPolicy, Type, MemberInfo[]>();
        
        private static readonly DoubleLookupDictionary<ISerializationPolicy, Type, Dictionary<string, MemberInfo>> MemberMapCache =
            new DoubleLookupDictionary<ISerializationPolicy, Type, Dictionary<string, MemberInfo>>();
        
        private static readonly object LOCK = new object();
        
        private static readonly HashSet<Type> PrimitiveArrayTypes = new HashSet<Type>(FastTypeComparer.Instance) {
            typeof(char),
            typeof(sbyte),
            typeof(short),
            typeof(int),
            typeof(long),
            typeof(byte),
            typeof(ushort),
            typeof(uint),
            typeof(ulong),
            typeof(decimal),
            typeof(bool),
            typeof(float),
            typeof(double),
            typeof(Guid)
        };
        
        private static readonly FieldInfo UnityObjectRuntimeErrorStringField;
        
        public static Dictionary<string, MemberInfo> GetSerializableMembersMap(Type type, ISerializationPolicy policy) {
            Dictionary<string, MemberInfo> result;
            
            if (policy == null) {
                policy = SerializationPolicies.Strict;
            }
            
            lock (LOCK) {
                if (MemberMapCache.TryGetInnerValue(policy, type, out result) == false) {
                    result = FindSerializableMembersMap(type, policy);
                    MemberMapCache.AddInner(policy, type, result);
                }
            }
            
            return result;
        }
        
        public static MemberInfo[] GetSerializableMembers(Type type, ISerializationPolicy policy) {
            MemberInfo[] result;
            
            if (policy == null) {
                policy = SerializationPolicies.Strict;
            }
            
            lock (LOCK) {
                if (MemberArrayCache.TryGetInnerValue(policy, type, out result) == false) {
                    List<MemberInfo> list = new List<MemberInfo>();
                    FindSerializableMembers(type, list, policy);
                    result = list.ToArray();
                    MemberArrayCache.AddInner(policy, type, result);
                }
            }
            
            return result;
        }
        
        public static bool IsPrimitiveType(Type type) {
            return type.IsPrimitive || type.IsEnum || type == typeof(decimal) || type == typeof(string) || type == typeof(Guid);
        }
        
        public static bool IsPrimitiveArrayType(Type type) {
            return PrimitiveArrayTypes.Contains(type);
        }
        
        public static Type GetContainedType(MemberInfo member) {
            if (member is FieldInfo) {
                return (member as FieldInfo).FieldType;
            } else if (member is PropertyInfo) {
                return (member as PropertyInfo).PropertyType;
            } else {
                throw new ArgumentException("Can't get the contained type of a " + member.GetType().Name);
            }
        }
        
        public static object GetMemberValue(MemberInfo member, object obj) {
            if (member is FieldInfo) {
                return (member as FieldInfo).GetValue(obj);
            } else if (member is PropertyInfo) {
                return (member as PropertyInfo).GetGetMethod(true).Invoke(obj, null);
            } else {
                throw new ArgumentException("Can't get the value of a " + member.GetType().Name);
            }
        }
        
        public static void SetMemberValue(MemberInfo member, object obj, object value) {
            if (member is FieldInfo) {
                (member as FieldInfo).SetValue(obj, value);
            } else if (member is PropertyInfo) {
                MethodInfo method = (member as PropertyInfo).GetSetMethod(true);
                
                if (method != null) {
                    method.Invoke(obj, new object[] { value });
                } else {
                    throw new ArgumentException("Property " + member.Name + " has no setter");
                }
            } else {
                throw new ArgumentException("Can't set the value of a " + member.GetType().Name);
            }
        }
        
        private static Dictionary<string, MemberInfo> FindSerializableMembersMap(Type type, ISerializationPolicy policy) {
            Dictionary<string, MemberInfo> map = GetSerializableMembers(type, policy).ToDictionary(n => n.Name, n => n);
            return map;
        }
        
        private static void FindSerializableMembers(Type type, List<MemberInfo> members, ISerializationPolicy policy) {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            
            if (type.BaseType != typeof(object) && type.BaseType != null) {
                FindSerializableMembers(type.BaseType, members, policy);
            }
            
            foreach (MemberInfo member in type.GetMembers(Flags).Where(n => n is FieldInfo || n is PropertyInfo)) {
                if (policy.ShouldSerializeMember(member)) {
                    bool nameAlreadyExists = members.Any(n => n.Name == member.Name);
                    
                    if (MemberIsPrivate(member) && nameAlreadyExists) {
                        members.Add(GetPrivateMemberAlias(member));
                    } else if (nameAlreadyExists) {
                        members.Add(GetPrivateMemberAlias(member));
                    } else {
                        members.Add(member);
                    }
                }
            }
        }
        
        public static MemberInfo GetPrivateMemberAlias(MemberInfo member, string prefixString = null, string separatorString = null) {
            if (member is FieldInfo) {
                if (separatorString != null) {
                    return new MemberAliasFieldInfo(member as FieldInfo, prefixString ?? member.DeclaringType.Name, separatorString);
                } else {
                    return new MemberAliasFieldInfo(member as FieldInfo, prefixString ?? member.DeclaringType.Name);
                }
            } else if (member is PropertyInfo) {
                if (separatorString != null) {
                    return new MemberAliasPropertyInfo(member as PropertyInfo, prefixString ?? member.DeclaringType.Name, separatorString);
                } else {
                    return new MemberAliasPropertyInfo(member as PropertyInfo, prefixString ?? member.DeclaringType.Name);
                }
            } else if (member is MethodInfo) {
                if (separatorString != null) {
                    return new MemberAliasMethodInfo(member as MethodInfo, prefixString ?? member.DeclaringType.Name, separatorString);
                } else {
                    return new MemberAliasMethodInfo(member as MethodInfo, prefixString ?? member.DeclaringType.Name);
                }
            }
            
            throw new NotImplementedException();
        }
        
        private static bool MemberIsPrivate(MemberInfo member) {
            if (member is FieldInfo) {
                return (member as FieldInfo).IsPrivate;
            } else if (member is PropertyInfo) {
                PropertyInfo prop = member as PropertyInfo;
                MethodInfo getter = prop.GetGetMethod();
                MethodInfo setter = prop.GetSetMethod();
                
                return getter != null && setter != null && getter.IsPrivate && setter.IsPrivate;
            } else if (member is MethodInfo) {
                return (member as MethodInfo).IsPrivate;
            }
            
            throw new NotImplementedException();
        }
    }
}