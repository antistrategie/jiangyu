using System;
using System.Globalization;
using System.Reflection;

namespace TinySerializer.Utilities.Misc {
    public sealed class MemberAliasMethodInfo : MethodInfo {
        public MethodInfo AliasedMethod => aliasedMethod;
        public override ICustomAttributeProvider ReturnTypeCustomAttributes => aliasedMethod.ReturnTypeCustomAttributes;
        public override RuntimeMethodHandle MethodHandle => aliasedMethod.MethodHandle;
        public override MethodAttributes Attributes => aliasedMethod.Attributes;
        public override Type ReturnType => aliasedMethod.ReturnType;
        public override Type DeclaringType => aliasedMethod.DeclaringType;
        public override string Name => mangledName;
        public override Type ReflectedType => aliasedMethod.ReflectedType;
        
        private MethodInfo aliasedMethod;
        private string mangledName;
        
        private const string FAKE_NAME_SEPARATOR_STRING = "+";
        
        public MemberAliasMethodInfo(MethodInfo method, string namePrefix) {
            aliasedMethod = method;
            mangledName = string.Concat(namePrefix, FAKE_NAME_SEPARATOR_STRING, aliasedMethod.Name);
        }
        
        public MemberAliasMethodInfo(MethodInfo method, string namePrefix, string separatorString) {
            aliasedMethod = method;
            mangledName = string.Concat(namePrefix, separatorString, aliasedMethod.Name);
        }
        
        public override MethodInfo GetBaseDefinition() => aliasedMethod.GetBaseDefinition();
        
        public override object[] GetCustomAttributes(bool inherit) => aliasedMethod.GetCustomAttributes(inherit);
        
        public override object[] GetCustomAttributes(Type attributeType, bool inherit) => aliasedMethod.GetCustomAttributes(attributeType, inherit);
        
        public override MethodImplAttributes GetMethodImplementationFlags() => aliasedMethod.GetMethodImplementationFlags();
        
        public override ParameterInfo[] GetParameters() => aliasedMethod.GetParameters();
        
        public override object Invoke(object obj, BindingFlags invokeAttr, Binder binder, object[] parameters, CultureInfo culture) {
            return aliasedMethod.Invoke(obj, invokeAttr, binder, parameters, culture);
        }
        
        public override bool IsDefined(Type attributeType, bool inherit) => aliasedMethod.IsDefined(attributeType, inherit);
    }
}