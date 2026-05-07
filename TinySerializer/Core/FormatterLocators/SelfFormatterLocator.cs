using System;
using TinySerializer.Core.FormatterLocators;
using TinySerializer.Core.Formatters;
using TinySerializer.Core.Misc;
using TinySerializer.Utilities.Extensions;

[assembly: RegisterFormatterLocator(typeof(SelfFormatterLocator), -60)]

namespace TinySerializer.Core.FormatterLocators {
    internal class SelfFormatterLocator : IFormatterLocator {
        public bool TryGetFormatter(Type type, FormatterLocationStep step, ISerializationPolicy policy, bool allowWeakFallbackFormatters, out IFormatter formatter) {
            formatter = null;
            
            if (!typeof(ISelfFormatter).IsAssignableFrom(type)) return false;
            
            if ((step == FormatterLocationStep.BeforeRegisteredFormatters && type.IsDefined<AlwaysFormatsSelfAttribute>())
                || step == FormatterLocationStep.AfterRegisteredFormatters) {
                try {
                    formatter = (IFormatter)Activator.CreateInstance(typeof(SelfFormatterFormatter<>).MakeGenericType(type));
                } catch (Exception ex) {
                #pragma warning disable CS0618
                    if (allowWeakFallbackFormatters && (ex is ExecutionEngineException || ex.GetBaseException() is ExecutionEngineException))
                #pragma warning restore CS0618
                    {
                        formatter = new WeakSelfFormatterFormatter(type);
                    } else throw;
                }
                
                return true;
            }
            
            return false;
        }
    }
}