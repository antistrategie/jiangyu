using System;
using TinySerializer.Core.FormatterLocators;
using TinySerializer.Core.Formatters;
using TinySerializer.Core.Misc;

[assembly: RegisterFormatterLocator(typeof(ArrayFormatterLocator), -80)]

namespace TinySerializer.Core.FormatterLocators {
    internal class ArrayFormatterLocator : IFormatterLocator {
        public bool TryGetFormatter(Type type, FormatterLocationStep step, ISerializationPolicy policy, bool allowWeakFallbackFormatters, out IFormatter formatter) {
            if (!type.IsArray) {
                formatter = null;
                return false;
            }
            
            Type elementType = type.GetElementType();
            
            if (type.GetArrayRank() == 1) {
                if (FormatterUtilities.IsPrimitiveArrayType(elementType)) {
                    try {
                        formatter = (IFormatter)Activator.CreateInstance(typeof(PrimitiveArrayFormatter<>).MakeGenericType(elementType));
                    } catch (Exception ex) {
                    #pragma warning disable CS0618
                        if (allowWeakFallbackFormatters && (ex is ExecutionEngineException || ex.GetBaseException() is ExecutionEngineException))
                    #pragma warning restore CS0618
                        {
                            formatter = new WeakPrimitiveArrayFormatter(type, elementType);
                        } else throw;
                    }
                } else {
                    try {
                        formatter = (IFormatter)Activator.CreateInstance(typeof(ArrayFormatter<>).MakeGenericType(elementType));
                    } catch (Exception ex) {
                    #pragma warning disable CS0618
                        if (allowWeakFallbackFormatters && (ex is ExecutionEngineException || ex.GetBaseException() is ExecutionEngineException))
                    #pragma warning restore CS0618
                        {
                            formatter = new WeakArrayFormatter(type, elementType);
                        } else throw;
                    }
                }
            } else {
                try {
                    formatter = (IFormatter)Activator.CreateInstance(typeof(MultiDimensionalArrayFormatter<,>).MakeGenericType(type, type.GetElementType()));
                } catch (Exception ex) {
                #pragma warning disable CS0618
                    if (allowWeakFallbackFormatters && (ex is ExecutionEngineException || ex.GetBaseException() is ExecutionEngineException))
                #pragma warning restore CS0618
                    {
                        formatter = new WeakMultiDimensionalArrayFormatter(type, elementType);
                    } else throw;
                }
            }
            
            return true;
        }
    }
}