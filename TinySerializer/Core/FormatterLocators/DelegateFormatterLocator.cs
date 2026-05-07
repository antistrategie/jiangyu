using System;
using TinySerializer.Core.FormatterLocators;
using TinySerializer.Core.Formatters;
using TinySerializer.Core.Misc;

[assembly: RegisterFormatterLocator(typeof(DelegateFormatterLocator), -50)]

namespace TinySerializer.Core.FormatterLocators {
    internal class DelegateFormatterLocator : IFormatterLocator {
        public bool TryGetFormatter(Type type, FormatterLocationStep step, ISerializationPolicy policy, bool allowWeakFallbackFormatters, out IFormatter formatter) {
            if (!typeof(Delegate).IsAssignableFrom(type)) {
                formatter = null;
                return false;
            }
            
            try {
                formatter = (IFormatter)Activator.CreateInstance(typeof(DelegateFormatter<>).MakeGenericType(type));
            } catch (Exception ex) {
            #pragma warning disable CS0618
                if (allowWeakFallbackFormatters && (ex is ExecutionEngineException || ex.GetBaseException() is ExecutionEngineException))
            #pragma warning restore CS0618
                {
                    formatter = new WeakDelegateFormatter(type);
                } else throw;
            }
            
            return true;
        }
    }
}