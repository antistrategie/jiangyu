using System;

namespace TinySerializer.Core.Misc {
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = true)]
    public sealed class AlwaysFormatsSelfAttribute : Attribute { }
}