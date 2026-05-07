using System;

namespace TinySerializer.Core.Misc {
    public class SerializationConfig {
        private readonly object LOCK = new object();
        private volatile ISerializationPolicy serializationPolicy;
        private volatile DebugContext debugContext;
        
        public SerializationConfig() {
            ResetToDefault();
        }
        
        public bool AllowDeserializeInvalidData = false;
        
        public ISerializationPolicy SerializationPolicy {
            get {
                if (serializationPolicy == null) {
                    lock (LOCK) {
                        if (serializationPolicy == null) {
                            serializationPolicy = SerializationPolicies.Unity;
                        }
                    }
                }
                
                return serializationPolicy;
            }
            
            set {
                lock (LOCK) {
                    serializationPolicy = value;
                }
            }
        }
        
        public DebugContext DebugContext {
            get {
                if (debugContext == null) {
                    lock (LOCK) {
                        if (debugContext == null) {
                            debugContext = new DebugContext();
                        }
                    }
                }
                
                return debugContext;
            }
            
            set {
                lock (LOCK) {
                    debugContext = value;
                }
            }
        }
        
        public void ResetToDefault() {
            lock (LOCK) {
                AllowDeserializeInvalidData = false;
                serializationPolicy = null;
                
                if (!ReferenceEquals(debugContext, null)) {
                    debugContext.ResetToDefault();
                }
            }
        }
    }
    
    public sealed class DebugContext {
        private readonly object LOCK = new object();
        
        private volatile ILogger logger;
        private volatile LoggingPolicy loggingPolicy;
        private volatile ErrorHandlingPolicy errorHandlingPolicy;
        
        public ILogger Logger {
            get {
                if (logger == null) {
                    lock (LOCK) {
                        if (logger == null) {
                            logger = DefaultLoggers.UnityLogger;
                        }
                    }
                }
                
                return logger;
            }
            set {
                lock (LOCK) {
                    logger = value;
                }
            }
        }
        
        public LoggingPolicy LoggingPolicy {
            get => loggingPolicy;
            set => loggingPolicy = value;
        }
        
        public ErrorHandlingPolicy ErrorHandlingPolicy {
            get => errorHandlingPolicy;
            set => errorHandlingPolicy = value;
        }
        
        public void LogWarning(string message) {
            if (errorHandlingPolicy == ErrorHandlingPolicy.ThrowOnWarningsAndErrors) {
                throw new SerializationAbortException("The following warning was logged during serialization or deserialization: " + (message ?? "EMPTY EXCEPTION MESSAGE"));
            }
            
            if (loggingPolicy == LoggingPolicy.LogWarningsAndErrors) {
                Logger.LogWarning(message);
            }
        }
        
        public void LogError(string message) {
            if (errorHandlingPolicy != ErrorHandlingPolicy.Resilient) {
                throw new SerializationAbortException("The following error was logged during serialization or deserialization: " + (message ?? "EMPTY EXCEPTION MESSAGE"));
            }
            
            if (loggingPolicy != LoggingPolicy.Silent) {
                Logger.LogError(message);
            }
        }
        
        public void LogException(Exception exception) {
            if (exception == null) {
                throw new ArgumentNullException("exception");
            }
            
            if (exception is SerializationAbortException) {
                throw exception;
            }
            
            ErrorHandlingPolicy policy = errorHandlingPolicy;
            
            if (policy != ErrorHandlingPolicy.Resilient) {
                throw new SerializationAbortException("An exception of type " + exception.GetType().Name + " occurred during serialization or deserialization.", exception);
            }
            
            if (loggingPolicy != LoggingPolicy.Silent) {
                Logger.LogException(exception);
            }
        }
        
        public void ResetToDefault() {
            lock (LOCK) {
                logger = null;
                loggingPolicy = default(LoggingPolicy);
                errorHandlingPolicy = default(ErrorHandlingPolicy);
            }
        }
    }
}