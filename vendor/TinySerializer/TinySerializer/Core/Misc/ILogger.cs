using System;

namespace TinySerializer.Core.Misc {
    public interface ILogger {
        void LogWarning(string warning);
        
        void LogError(string error);
        
        void LogException(Exception exception);
    }
}