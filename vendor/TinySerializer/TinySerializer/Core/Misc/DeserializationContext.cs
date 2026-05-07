using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using TinySerializer.Utilities.Misc;

namespace TinySerializer.Core.Misc {
    public sealed class DeserializationContext : ICacheNotificationReceiver {
        private SerializationConfig config;
        private Dictionary<int, object> internalIdReferenceMap = new Dictionary<int, object>(128);
        private StreamingContext streamingContext;
        private IFormatterConverter formatterConverter;
        private TwoWaySerializationBinder binder;
        
        public DeserializationContext() : this(new StreamingContext(), new FormatterConverter()) { }
        
        public DeserializationContext(StreamingContext context) : this(context, new FormatterConverter()) { }
        
        public DeserializationContext(FormatterConverter formatterConverter) : this(new StreamingContext(), formatterConverter) { }
        
        public DeserializationContext(StreamingContext context, FormatterConverter formatterConverter) {
            if (formatterConverter == null) {
                throw new ArgumentNullException("formatterConverter");
            }
            
            streamingContext = context;
            this.formatterConverter = formatterConverter;
            
            Reset();
        }
        
        public TwoWaySerializationBinder Binder {
            get {
                if (binder == null) {
                    binder = DefaultSerializationBinder.Default;
                }
                
                return binder;
            }
            
            set {
                binder = value;
            }
        }
        
        public IExternalStringReferenceResolver StringReferenceResolver { get; set; }
        
        public IExternalGuidReferenceResolver GuidReferenceResolver { get; set; }
        
        public IExternalIndexReferenceResolver IndexReferenceResolver { get; set; }
        
        public StreamingContext StreamingContext { get { return streamingContext; } }
        
        public IFormatterConverter FormatterConverter { get { return formatterConverter; } }
        
        public SerializationConfig Config {
            get {
                if (config == null) {
                    config = new SerializationConfig();
                }
                
                return config;
            }
            
            set {
                config = value;
            }
        }
        
        public void RegisterInternalReference(int id, object reference) {
            internalIdReferenceMap[id] = reference;
        }
        
        public object GetInternalReference(int id) {
            object result;
            internalIdReferenceMap.TryGetValue(id, out result);
            return result;
        }
        
        public object GetExternalObject(int index) {
            if (IndexReferenceResolver == null) {
                Config.DebugContext.LogWarning("Tried to resolve external reference by index (" + index
                                               + "), but no index reference resolver is assigned to the deserialization context. External reference has been lost.");
                
                return null;
            }
            
            object result;
            
            if (IndexReferenceResolver.TryResolveReference(index, out result)) {
                return result;
            }
            
            Config.DebugContext.LogWarning("Failed to resolve external reference by index (" + index + "); the index resolver could not resolve the index. Reference lost.");
            return null;
        }
        
        public object GetExternalObject(Guid guid) {
            if (GuidReferenceResolver == null) {
                Config.DebugContext.LogWarning("Tried to resolve external reference by guid (" + guid
                                               + "), but no guid reference resolver is assigned to the deserialization context. External reference has been lost.");
                
                return null;
            }
            
            IExternalGuidReferenceResolver resolver = GuidReferenceResolver;
            object result;
            
            while (resolver != null) {
                if (resolver.TryResolveReference(guid, out result)) {
                    return result;
                }
                
                resolver = resolver.NextResolver;
            }
            
            Config.DebugContext.LogWarning("Failed to resolve external reference by guid (" + guid + "); no guid resolver could resolve the guid. Reference lost.");
            return null;
        }
        
        public object GetExternalObject(string id) {
            if (StringReferenceResolver == null) {
                Config.DebugContext.LogWarning("Tried to resolve external reference by string (" + id
                                               + "), but no string reference resolver is assigned to the deserialization context. External reference has been lost.");
                
                return null;
            }
            
            IExternalStringReferenceResolver resolver = StringReferenceResolver;
            object result;
            
            while (resolver != null) {
                if (resolver.TryResolveReference(id, out result)) {
                    return result;
                }
                
                resolver = resolver.NextResolver;
            }
            
            Config.DebugContext.LogWarning("Failed to resolve external reference by string (" + id + "); no string resolver could resolve the string. Reference lost.");
            return null;
        }
        
        public void Reset() {
            if (!ReferenceEquals(config, null)) {
                config.ResetToDefault();
            }
            
            internalIdReferenceMap.Clear();
            IndexReferenceResolver = null;
            GuidReferenceResolver = null;
            StringReferenceResolver = null;
            binder = null;
        }
        
        void ICacheNotificationReceiver.OnFreed() {
            Reset();
        }
        
        void ICacheNotificationReceiver.OnClaimed() { }
    }
}