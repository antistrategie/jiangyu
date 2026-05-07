using System;
using System.Globalization;
using System.Reflection;

namespace TinySerializer.Utilities.Misc {
    public sealed class MemberAliasPropertyInfo : PropertyInfo {
        public PropertyInfo AliasedProperty => aliasedProperty;
        public override Module Module => aliasedProperty.Module;
        public override int MetadataToken => aliasedProperty.MetadataToken;
        public override string Name => mangledName;
        public override Type DeclaringType => aliasedProperty.DeclaringType;
        public override Type ReflectedType => aliasedProperty.ReflectedType;
        public override Type PropertyType => aliasedProperty.PropertyType;
        public override PropertyAttributes Attributes => aliasedProperty.Attributes;
        public override bool CanRead => aliasedProperty.CanRead;
        public override bool CanWrite => aliasedProperty.CanWrite;
        
        private PropertyInfo aliasedProperty;
        private string mangledName;
        
        private const string FakeNameSeparatorString = "+";
        
        public MemberAliasPropertyInfo(PropertyInfo prop, string namePrefix) {
            aliasedProperty = prop;
            mangledName = string.Concat(namePrefix, FakeNameSeparatorString, aliasedProperty.Name);
        }
        
        public MemberAliasPropertyInfo(PropertyInfo prop, string namePrefix, string separatorString) {
            aliasedProperty = prop;
            mangledName = string.Concat(namePrefix, separatorString, aliasedProperty.Name);
        }
        
        public override object[] GetCustomAttributes(bool inherit) => aliasedProperty.GetCustomAttributes(inherit);
        
        public override object[] GetCustomAttributes(Type attributeType, bool inherit) => aliasedProperty.GetCustomAttributes(attributeType, inherit);
        
        public override bool IsDefined(Type attributeType, bool inherit) => aliasedProperty.IsDefined(attributeType, inherit);
        
        public override MethodInfo[] GetAccessors(bool nonPublic) => aliasedProperty.GetAccessors(nonPublic);
        
        public override MethodInfo GetGetMethod(bool nonPublic) => aliasedProperty.GetGetMethod(nonPublic);
        
        public override ParameterInfo[] GetIndexParameters() => aliasedProperty.GetIndexParameters();
        
        public override MethodInfo GetSetMethod(bool nonPublic) => aliasedProperty.GetSetMethod(nonPublic);
        
        public override object GetValue(object obj, BindingFlags invokeAttr, Binder binder, object[] index, CultureInfo culture) {
            return aliasedProperty.GetValue(obj, invokeAttr, binder, index, culture);
        }
        
        public override void SetValue(object obj, object value, BindingFlags invokeAttr, Binder binder, object[] index, CultureInfo culture) {
            aliasedProperty.SetValue(obj, value, invokeAttr, binder, index, culture);
        }
    }
}