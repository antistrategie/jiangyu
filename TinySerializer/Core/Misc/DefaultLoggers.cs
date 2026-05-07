using System;

namespace TinySerializer.Core.Misc {
    public static class DefaultLoggers {
        private static readonly object LOCK = new object();
        private static volatile ILogger unityLogger;
        
        public static ILogger DefaultLogger {
            get {
                return UnityLogger;
            }
        }
        
        public static ILogger UnityLogger {
            get {
                if (unityLogger == null) {
                    lock (LOCK) {
                        if (unityLogger == null) {
                            unityLogger = new CustomLogger(LogWarning, LogError, LogException);
                        }
                    }
                }
                
                return unityLogger;
            }
        }
        
        private static void LogWarning(string message) => Console.WriteLine($"Warning: {message}");
        
        private static void LogError(string message) => Console.WriteLine($"Error: {message}");
        
        private static void LogException(Exception exception) => Console.WriteLine($"Exception: {exception}");
    }
}