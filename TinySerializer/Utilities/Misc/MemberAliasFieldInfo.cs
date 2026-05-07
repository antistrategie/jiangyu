using System;
using System.Globalization;
using System.Reflection;

namespace TinySerializer.Utilities.Misc {
    public sealed class MemberAliasFieldInfo : FieldInfo {
        public FieldInfo AliasedField => aliasedField;
        public override Module Module => aliasedField.Module;
        public override int MetadataToken => aliasedField.MetadataToken;
        public override string Name => mangledName;
        public override Type DeclaringType => aliasedField.DeclaringType;
        public override Type ReflectedType => aliasedField.ReflectedType;
        public override Type FieldType => aliasedField.FieldType;
        public override RuntimeFieldHandle FieldHandle => aliasedField.FieldHandle;
        public override FieldAttributes Attributes => aliasedField.Attributes;
        
        private FieldInfo aliasedField;
        private string mangledName;
        
        private const string FAKE_NAME_SEPARATOR_STRING = "+";
        
        public MemberAliasFieldInfo(FieldInfo field, string namePrefix) {
            aliasedField = field;
            mangledName = string.Concat(namePrefix, FAKE_NAME_SEPARATOR_STRING, aliasedField.Name);
        }
        
        public MemberAliasFieldInfo(FieldInfo field, string namePrefix, string separatorString) {
            aliasedField = field;
            mangledName = string.Concat(namePrefix, separatorString, aliasedField.Name);
        }
        
        public override object[] GetCustomAttributes(bool inherit) => aliasedField.GetCustomAttributes(inherit);
        
        public override object[] GetCustomAttributes(Type attributeType, bool inherit) => aliasedField.GetCustomAttributes(attributeType, inherit);
        
        public override bool IsDefined(Type attributeType, bool inherit) => aliasedField.IsDefined(attributeType, inherit);
        
        public override object GetValue(object obj) => aliasedField.GetValue(obj);
        
        public override void SetValue(object obj, object value, BindingFlags invokeAttr, Binder binder, CultureInfo culture) {
            aliasedField.SetValue(obj, value, invokeAttr, binder, culture);
        }
    }
}