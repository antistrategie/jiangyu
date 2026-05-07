using System.IO;
using TinySerializer.Utilities;
using TinySerializer.Utilities.Misc;

namespace TinySerializer.Core.Misc {
    internal sealed class CachedMemoryStream : ICacheNotificationReceiver {
        public static int InitialCapacity = 1024 * 1;
        public static int MaxCapacity = 1024 * 32;
        
        private MemoryStream memoryStream;
        
        public MemoryStream MemoryStream {
            get {
                if (!memoryStream.CanRead) {
                    memoryStream = new MemoryStream(InitialCapacity);
                }
                
                return memoryStream;
            }
        }
        
        public CachedMemoryStream() {
            memoryStream = new MemoryStream(InitialCapacity);
        }
        
        public void OnFreed() {
            memoryStream.SetLength(0);
            memoryStream.Position = 0;
            
            if (memoryStream.Capacity > MaxCapacity) {
                memoryStream.Capacity = MaxCapacity;
            }
        }
        
        public void OnClaimed() {
            memoryStream.SetLength(0);
            memoryStream.Position = 0;
        }
        
        public static Cache<CachedMemoryStream> Claim(byte[] bytes = null) {
            Cache<CachedMemoryStream> cache = Cache<CachedMemoryStream>.Claim();
            
            if (bytes != null) {
                cache.Value.MemoryStream.Write(bytes, 0, bytes.Length);
                cache.Value.MemoryStream.Position = 0;
            }
            
            return cache;
        }
    }
}