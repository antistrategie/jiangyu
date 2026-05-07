using System;
using System.Collections.Generic;

namespace TinySerializer.Core.Misc {
    public sealed class Buffer<T> : IDisposable {
        private static readonly object LOCK = new object();
        private static readonly List<Buffer<T>> FreeBuffers = new List<Buffer<T>>();
        
        private int count;
        private T[] array;
        private volatile bool isFree;
        
        private Buffer(int count) {
            array = new T[count];
            this.count = count;
            isFree = false;
        }
        
        public int Count {
            get {
                if (isFree) {
                    throw new InvalidOperationException("Cannot access a buffer while it is freed.");
                }
                
                return count;
            }
        }
        
        public T[] Array {
            get {
                if (isFree) {
                    throw new InvalidOperationException("Cannot access a buffer while it is freed.");
                }
                
                return array;
            }
        }
        
        public bool IsFree { get { return isFree; } }
        
        public static Buffer<T> Claim(int minimumCapacity) {
            if (minimumCapacity < 0) {
                throw new ArgumentException("Requested size of buffer must be larger than or equal to 0.");
            }
            
            if (minimumCapacity < 256) {
                minimumCapacity = 256;
            }
            
            Buffer<T> result = null;
            
            lock (LOCK) {
                for (int i = 0; i < FreeBuffers.Count; i++) {
                    Buffer<T> buffer = FreeBuffers[i];
                    
                    if (buffer != null && buffer.count >= minimumCapacity) {
                        result = buffer;
                        result.isFree = false;
                        FreeBuffers[i] = null;
                        break;
                    }
                }
            }
            
            if (result == null) {
                result = new Buffer<T>(NextPowerOfTwo(minimumCapacity));
            }
            
            return result;
        }
        
        public static void Free(Buffer<T> buffer) {
            if (buffer == null) {
                throw new ArgumentNullException("buffer");
            }
            
            if (buffer.isFree == false) {
                lock (LOCK) {
                    if (buffer.isFree == false) {
                        buffer.isFree = true;
                        
                        bool added = false;
                        
                        for (int i = 0; i < FreeBuffers.Count; i++) {
                            if (FreeBuffers[i] == null) {
                                FreeBuffers[i] = buffer;
                                added = true;
                                break;
                            }
                        }
                        
                        if (!added) {
                            FreeBuffers.Add(buffer);
                        }
                    }
                }
            }
        }
        
        public void Free() {
            Free(this);
        }
        
        public void Dispose() {
            Free(this);
        }
        
        private static int NextPowerOfTwo(int v) {
            v--;
            v |= v >> 1;
            v |= v >> 2;
            v |= v >> 4;
            v |= v >> 8;
            v |= v >> 16;
            v++;
            return v;
        }
    }
}