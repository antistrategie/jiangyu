using System;
using System.Reflection;

namespace TinySerializer.Utilities.Extensions {
    public static class MemberInfoExtensions {
        public static bool IsDefined<T>(this ICustomAttributeProvider member, bool inherit) where T : Attribute {
            try {
                return member.IsDefined(typeof(T), inherit);
            } catch {
                return false;
            }
        }
        
        public static bool IsDefined<T>(this ICustomAttributeProvider member) where T : Attribute {
            return IsDefined<T>(member, false);
        }
        
        public static string GetNiceName(this MemberInfo member) {
            MethodBase method = member as MethodBase;
            string result;
            
            if (method != null) {
                result = method.GetFullName();
            } else {
                result = member.Name;
            }
            
            return result.ToTitleCase();
        }
    }
}