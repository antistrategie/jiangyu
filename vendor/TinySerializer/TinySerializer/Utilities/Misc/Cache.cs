using System;
using System.Threading;

namespace TinySerializer.Utilities.Misc {
    public interface ICache : IDisposable {
        object Value { get; }
    }
    
    public sealed class Cache<T> : ICache where T : class, new() {
        private static readonly bool IsNotificationReceiver = typeof(ICacheNotificationReceiver).IsAssignableFrom(typeof(T));
        private static object[] FreeValues = new object[4];
        
        private bool isFree;
        
        private static volatile int THREAD_LOCK_TOKEN = 0;
        
        private static int maxCacheSize = 5;
        
        public static int MaxCacheSize {
            get => maxCacheSize;
            set => maxCacheSize = Math.Max(1, value);
        }
        
        private Cache() {
            Value = new T();
            isFree = false;
        }
        
        public T Value;
        
        public bool IsFree => isFree;
        
        object ICache.Value => Value;
        
        public static Cache<T> Claim() {
            Cache<T> result = null;
            
            while (true) {
                if (Interlocked.CompareExchange(ref THREAD_LOCK_TOKEN, 1, 0) == 0) {
                    break;
                }
            }
            
            object[] freeValues = FreeValues;
            int length = freeValues.Length;
            
            for (int i = 0; i < length; i++) {
                result = (Cache<T>)freeValues[i];
                
                if (!ReferenceEquals(result, null)) {
                    freeValues[i] = null;
                    result.isFree = false;
                    break;
                }
            }
            
            THREAD_LOCK_TOKEN = 0;
            
            if (result == null) {
                result = new Cache<T>();
            }
            
            if (IsNotificationReceiver) {
                (result.Value as ICacheNotificationReceiver).OnClaimed();
            }
            
            return result;
        }
        
        public static void Release(Cache<T> cache) {
            if (cache == null) {
                throw new ArgumentNullException("cache");
            }
            
            if (cache.isFree) return;
            
            if (IsNotificationReceiver) {
                (cache.Value as ICacheNotificationReceiver).OnFreed();
            }
            
            while (true) {
                if (Interlocked.CompareExchange(ref THREAD_LOCK_TOKEN, 1, 0) == 0) {
                    break;
                }
            }
            
            if (cache.isFree) {
                THREAD_LOCK_TOKEN = 0;
                return;
            }
            
            
            cache.isFree = true;
            
            object[] freeValues = FreeValues;
            int length = freeValues.Length;
            
            bool added = false;
            
            for (int i = 0; i < length; i++) {
                if (ReferenceEquals(freeValues[i], null)) {
                    freeValues[i] = cache;
                    added = true;
                    break;
                }
            }
            
            if (!added && length < MaxCacheSize) {
                object[] newArr = new object[length * 2];
                
                for (int i = 0; i < length; i++) {
                    newArr[i] = freeValues[i];
                }
                
                newArr[length] = cache;
                
                FreeValues = newArr;
            }
            
            THREAD_LOCK_TOKEN = 0;
            
        }
        
        public static implicit operator T(Cache<T> cache) {
            if (cache == null) {
                return default(T);
            }
            
            return cache.Value;
        }
        
        public void Release() => Release(this);
        
        void IDisposable.Dispose() => Release(this);
    }
}