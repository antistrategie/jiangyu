using System.Reflection;

namespace TinySerializer.Utilities.Extensions {
    public static class PropertyInfoExtensions {
        public static bool IsAutoProperty(this PropertyInfo propInfo, bool allowVirtual = false) {
            if (!(propInfo.CanWrite && propInfo.CanRead)) {
                return false;
            }
            
            if (!allowVirtual) {
                MethodInfo getter = propInfo.GetGetMethod(true);
                MethodInfo setter = propInfo.GetSetMethod(true);
                
                if ((getter != null && (getter.IsAbstract || getter.IsVirtual)) || (setter != null && (setter.IsAbstract || setter.IsVirtual))) {
                    return false;
                }
            }
            
            BindingFlags flag = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            string compilerGeneratedName = "<" + propInfo.Name + ">";
            FieldInfo[] fields = propInfo.DeclaringType.GetFields(flag);
            
            for (int i = 0; i < fields.Length; i++) {
                if (fields[i].Name.Contains(compilerGeneratedName)) {
                    return true;
                }
            }
            
            return false;
        }
    }
}