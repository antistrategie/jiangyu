using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using TinySerializer.Utilities;
using TinySerializer.Utilities.Misc;

namespace TinySerializer.Core.Misc {
    public sealed class SerializationContext : ICacheNotificationReceiver {
        private SerializationConfig config;
        private Dictionary<object, int> internalReferenceIdMap = new Dictionary<object, int>(128, ReferenceEqualityComparer<object>.Default);
        private StreamingContext streamingContext;
        private IFormatterConverter formatterConverter;
        private TwoWaySerializationBinder binder;
        
        public SerializationContext() : this(new StreamingContext(), new FormatterConverter()) { }
        
        public SerializationContext(StreamingContext context) : this(context, new FormatterConverter()) { }
        
        public SerializationContext(FormatterConverter formatterConverter) : this(new StreamingContext(), formatterConverter) { }
        
        public SerializationContext(StreamingContext context, FormatterConverter formatterConverter) {
            if (formatterConverter == null) {
                throw new ArgumentNullException("formatterConverter");
            }
            
            streamingContext = context;
            this.formatterConverter = formatterConverter;
            
            ResetToDefault();
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
        
        public StreamingContext StreamingContext { get { return streamingContext; } }
        
        public IFormatterConverter FormatterConverter { get { return formatterConverter; } }
        
        public IExternalIndexReferenceResolver IndexReferenceResolver { get; set; }
        
        public IExternalStringReferenceResolver StringReferenceResolver { get; set; }
        
        public IExternalGuidReferenceResolver GuidReferenceResolver { get; set; }
        
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
        
        public bool TryRegisterInternalReference(object reference, out int id) {
            if (internalReferenceIdMap.TryGetValue(reference, out id) == false) {
                id = internalReferenceIdMap.Count;
                internalReferenceIdMap.Add(reference, id);
                return true;
            }
            
            return false;
        }
        
        public bool TryRegisterExternalReference(object obj, out int index) {
            if (IndexReferenceResolver == null) {
                index = -1;
                return false;
            }
            
            if (IndexReferenceResolver.CanReference(obj, out index)) {
                return true;
            }
            
            index = -1;
            return false;
        }
        
        /// <summary>
        /// Tries to register an external guid reference.
        /// </summary>
        /// <param name="obj">The object to reference.</param>
        /// <param name="guid">The guid of the referenced object.</param>
        /// <returns><c>true</c> if the object could be referenced by guid; otherwise, <c>false</c>.</returns>
        public bool TryRegisterExternalReference(object obj, out Guid guid) {
            if (GuidReferenceResolver == null) {
                guid = Guid.Empty;
                return false;
            }
            
            IExternalGuidReferenceResolver resolver = GuidReferenceResolver;
            
            while (resolver != null) {
                if (resolver.CanReference(obj, out guid)) {
                    return true;
                }
                
                resolver = resolver.NextResolver;
            }
            
            guid = Guid.Empty;
            return false;
        }
        
        public bool TryRegisterExternalReference(object obj, out string id) {
            if (StringReferenceResolver == null) {
                id = null;
                return false;
            }
            
            IExternalStringReferenceResolver resolver = StringReferenceResolver;
            
            while (resolver != null) {
                if (resolver.CanReference(obj, out id)) {
                    return true;
                }
                
                resolver = resolver.NextResolver;
            }
            
            id = null;
            return false;
        }
        
        public void ResetToDefault() {
            if (!ReferenceEquals(config, null)) {
                config.ResetToDefault();
            }
            
            internalReferenceIdMap.Clear();
            IndexReferenceResolver = null;
            GuidReferenceResolver = null;
            StringReferenceResolver = null;
            binder = null;
        }
        
        void ICacheNotificationReceiver.OnFreed() {
            ResetToDefault();
        }
        
        void ICacheNotificationReceiver.OnClaimed() { }
    }
}