using System;
using TinySerializer.Core.Formatters;
using TinySerializer.Core.Misc;

namespace TinySerializer.Core.FormatterLocators {
    public interface IFormatterLocator {
        bool TryGetFormatter(Type type, FormatterLocationStep step, ISerializationPolicy policy, bool allowWeakFallbackFormatters, out IFormatter formatter);
    }
}