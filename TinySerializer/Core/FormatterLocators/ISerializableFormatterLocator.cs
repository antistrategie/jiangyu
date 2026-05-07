using System;
using System.Runtime.Serialization;
using TinySerializer.Core.FormatterLocators;
using TinySerializer.Core.Formatters;
using TinySerializer.Core.Misc;
using IFormatter = TinySerializer.Core.Formatters.IFormatter;

[assembly: RegisterFormatterLocator(typeof(ISerializableFormatterLocator), -110)]

namespace TinySerializer.Core.FormatterLocators {
    internal class ISerializableFormatterLocator : IFormatterLocator {
        public bool TryGetFormatter(Type type, FormatterLocationStep step, ISerializationPolicy policy, bool allowWeakFallbackFormatters, out IFormatter formatter) {
            if (step != FormatterLocationStep.AfterRegisteredFormatters || !typeof(ISerializable).IsAssignableFrom(type)) {
                formatter = null;
                return false;
            }
            
            try {
                formatter = (IFormatter)Activator.CreateInstance(typeof(SerializableFormatter<>).MakeGenericType(type));
            } catch (Exception ex) {
            #pragma warning disable CS0618
                if (allowWeakFallbackFormatters && (ex is ExecutionEngineException || ex.GetBaseException() is ExecutionEngineException))
            #pragma warning restore CS0618
                {
                    formatter = new WeakSerializableFormatter(type);
                } else throw;
            }
            
            return true;
        }
    }
}