using System;

namespace TinySerializer.Core.Formatters {
    /// <summary>
    /// Indicates that this formatter type has been emitted. Never put this on a type!
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class EmittedFormatterAttribute : Attribute { }
}