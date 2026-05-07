using System;
using System.Collections.Generic;

namespace TinySerializer.Utilities.Misc {
    public class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class {
        public static readonly ReferenceEqualityComparer<T> Default = new ReferenceEqualityComparer<T>();
        
        public bool Equals(T x, T y) => ReferenceEquals(x, y);
        
        public int GetHashCode(T obj) {
            try {
                return obj.GetHashCode();
            } catch (NullReferenceException) {
                return -1;
            }
        }
    }
}