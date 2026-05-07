using System;

namespace TinySerializer.Core.Misc {
    public interface IAskIfCanFormatTypes {
        bool CanFormatType(Type type);
    }
}