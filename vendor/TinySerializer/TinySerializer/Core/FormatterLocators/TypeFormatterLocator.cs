using System;
using TinySerializer.Core.FormatterLocators;
using TinySerializer.Core.Formatters;
using TinySerializer.Core.Misc;

[assembly: RegisterFormatterLocator(typeof(TypeFormatterLocator), -70)]

namespace TinySerializer.Core.FormatterLocators {
    internal class TypeFormatterLocator : IFormatterLocator {
        public bool TryGetFormatter(Type type, FormatterLocationStep step, ISerializationPolicy policy, bool allowWeakFallbackFormatters, out IFormatter formatter) {
            if (!typeof(Type).IsAssignableFrom(type)) {
                formatter = null;
                return false;
            }
            
            formatter = new TypeFormatter();
            return true;
        }
    }
}