using System;

namespace TinySerializer.Core.Misc {
    public class CustomLogger : ILogger {
        private Action<string> logWarningDelegate;
        private Action<string> logErrorDelegate;
        private Action<Exception> logExceptionDelegate;
        
        public CustomLogger(Action<string> logWarningDelegate, Action<string> logErrorDelegate, Action<Exception> logExceptionDelegate) {
            if (logWarningDelegate == null) {
                throw new ArgumentNullException("logWarningDelegate");
            }
            
            if (logErrorDelegate == null) {
                throw new ArgumentNullException("logErrorDelegate");
            }
            
            if (logExceptionDelegate == null) {
                throw new ArgumentNullException("logExceptionDelegate");
            }
            
            this.logWarningDelegate = logWarningDelegate;
            this.logErrorDelegate = logErrorDelegate;
            this.logExceptionDelegate = logExceptionDelegate;
        }
        
        public void LogWarning(string warning) {
            logWarningDelegate(warning);
        }
        
        public void LogError(string error) {
            logErrorDelegate(error);
        }
        
        public void LogException(Exception exception) {
            logExceptionDelegate(exception);
        }
    }
}