using System;
using System.Collections.Generic;

namespace TinySerializer.Utilities.Extensions {
    public static class GarbageFreeIterators {
        public static DictionaryIterator<T1, T2> GFIterator<T1, T2>(this Dictionary<T1, T2> dictionary) {
            return new DictionaryIterator<T1, T2>(dictionary);
        }
        
        public struct DictionaryIterator<T1, T2> : IDisposable {
            private Dictionary<T1, T2> dictionary;
            private Dictionary<T1, T2>.Enumerator enumerator;
            private bool isNull;
            
            public DictionaryIterator(Dictionary<T1, T2> dictionary) {
                isNull = dictionary == null;
                
                if (isNull) {
                    this.dictionary = null;
                    enumerator = new Dictionary<T1, T2>.Enumerator();
                } else {
                    this.dictionary = dictionary;
                    enumerator = this.dictionary.GetEnumerator();
                }
            }
            
            public DictionaryIterator<T1, T2> GetEnumerator() {
                return this;
            }
            
            public KeyValuePair<T1, T2> Current => enumerator.Current;
            
            public bool MoveNext() {
                if (isNull) {
                    return false;
                }
                
                return enumerator.MoveNext();
            }
            
            public void Dispose() => enumerator.Dispose();
        }
    }
}