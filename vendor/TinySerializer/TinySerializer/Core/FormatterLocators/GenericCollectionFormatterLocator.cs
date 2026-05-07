using System;
using TinySerializer.Core.FormatterLocators;
using TinySerializer.Core.Formatters;
using TinySerializer.Core.Misc;

[assembly: RegisterFormatterLocator(typeof(GenericCollectionFormatterLocator), -100)]

namespace TinySerializer.Core.FormatterLocators {
    internal class GenericCollectionFormatterLocator : IFormatterLocator {
        public bool TryGetFormatter(Type type, FormatterLocationStep step, ISerializationPolicy policy, bool allowWeakFallbackFormatters, out IFormatter formatter) {
            Type elementType;
            
            if (step != FormatterLocationStep.AfterRegisteredFormatters || !GenericCollectionFormatter.CanFormat(type, out elementType)) {
                formatter = null;
                return false;
            }
            
            try {
                formatter = (IFormatter)Activator.CreateInstance(typeof(GenericCollectionFormatter<,>).MakeGenericType(type, elementType));
            } catch (Exception ex) {
            #pragma warning disable CS0618
                if (allowWeakFallbackFormatters && (ex is ExecutionEngineException || ex.GetBaseException() is ExecutionEngineException))
            #pragma warning restore CS0618
                {
                    formatter = new WeakGenericCollectionFormatter(type, elementType);
                } else throw;
            }
            
            return true;
        }
    }
}