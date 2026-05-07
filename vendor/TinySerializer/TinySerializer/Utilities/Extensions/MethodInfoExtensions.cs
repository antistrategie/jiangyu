using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace TinySerializer.Utilities.Extensions {
    public static class MethodInfoExtensions {
        public static string GetFullName(this MethodBase method, string extensionMethodPrefix) {
            StringBuilder builder = new StringBuilder();
            bool isExtensionMethod = method.IsExtensionMethod();
            
            if (isExtensionMethod) {
                builder.Append(extensionMethodPrefix);
            }
            
            builder.Append(method.Name);
            builder.Append("(");
            builder.Append(method.GetParamsNames());
            builder.Append(")");
            return builder.ToString();
        }
        
        public static string GetParamsNames(this MethodBase method) {
            ParameterInfo[] pinfos = method.IsExtensionMethod() ? method.GetParameters().Skip(1).ToArray() : method.GetParameters();
            StringBuilder builder = new StringBuilder();
            
            for (int i = 0, len = pinfos.Length; i < len; i++) {
                ParameterInfo param = pinfos[i];
                string paramTypeName = param.ParameterType.GetNiceName();
                builder.Append(paramTypeName);
                builder.Append(" ");
                builder.Append(param.Name);
                
                if (i < len - 1) {
                    builder.Append(", ");
                }
            }
            
            return builder.ToString();
        }
        
        public static string GetFullName(this MethodBase method) {
            return GetFullName(method, "[ext] ");
        }
        
        public static bool IsExtensionMethod(this MethodBase method) {
            Type type = method.DeclaringType;
            return type.IsSealed && !type.IsGenericType && !type.IsNested && method.IsDefined(typeof(ExtensionAttribute), false);
        }
    }
}