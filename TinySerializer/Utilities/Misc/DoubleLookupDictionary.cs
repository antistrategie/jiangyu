using System;
using System.Collections.Generic;
using TinySerializer.Utilities.Extensions;

namespace TinySerializer.Utilities.Misc {
    [Serializable]
    public class DoubleLookupDictionary<TFirstKey, TSecondKey, TValue> : Dictionary<TFirstKey, Dictionary<TSecondKey, TValue>> {
        private readonly IEqualityComparer<TSecondKey> secondKeyComparer;
        
        public DoubleLookupDictionary() {
            secondKeyComparer = EqualityComparer<TSecondKey>.Default;
        }
        
        public DoubleLookupDictionary(IEqualityComparer<TFirstKey> firstKeyComparer, IEqualityComparer<TSecondKey> secondKeyComparer) : base(firstKeyComparer) {
            this.secondKeyComparer = secondKeyComparer;
        }
        
        public new Dictionary<TSecondKey, TValue> this[TFirstKey firstKey] {
            get {
                Dictionary<TSecondKey, TValue> innerDict;
                
                if (!TryGetValue(firstKey, out innerDict)) {
                    innerDict = new Dictionary<TSecondKey, TValue>(secondKeyComparer);
                    Add(firstKey, innerDict);
                }
                
                return innerDict;
            }
        }
        
        public int InnerCount(TFirstKey firstKey) {
            Dictionary<TSecondKey, TValue> innerDict;
            
            if (TryGetValue(firstKey, out innerDict)) {
                return innerDict.Count;
            }
            
            return 0;
        }
        
        public int TotalInnerCount() {
            int count = 0;
            
            if (Count > 0) {
                foreach (Dictionary<TSecondKey, TValue> innerDict in Values) {
                    count += innerDict.Count;
                }
            }
            
            return count;
        }
        
        public bool ContainsKeys(TFirstKey firstKey, TSecondKey secondKey) {
            Dictionary<TSecondKey, TValue> innerDict;
            
            return TryGetValue(firstKey, out innerDict) && innerDict.ContainsKey(secondKey);
        }
        
        public bool TryGetInnerValue(TFirstKey firstKey, TSecondKey secondKey, out TValue value) {
            Dictionary<TSecondKey, TValue> innerDict;
            
            if (TryGetValue(firstKey, out innerDict) && innerDict.TryGetValue(secondKey, out value)) {
                return true;
            }
            
            value = default;
            return false;
        }
        
        public TValue AddInner(TFirstKey firstKey, TSecondKey secondKey, TValue value) {
            if (ContainsKeys(firstKey, secondKey)) {
                throw new ArgumentException("An element with the same keys already exists in the " + GetType().GetNiceName() + ".");
            }
            
            return this[firstKey][secondKey] = value;
        }
        
        public bool RemoveInner(TFirstKey firstKey, TSecondKey secondKey) {
            Dictionary<TSecondKey, TValue> innerDict;
            
            if (TryGetValue(firstKey, out innerDict)) {
                bool removed = innerDict.Remove(secondKey);
                
                if (innerDict.Count == 0) {
                    Remove(firstKey);
                }
                
                return removed;
            }
            
            return false;
        }
        
        public void RemoveWhere(Func<TValue, bool> predicate) {
            List<TFirstKey> toRemoveBufferFirstKey = new List<TFirstKey>();
            List<TSecondKey> toRemoveBufferSecondKey = new List<TSecondKey>();
            
            foreach (KeyValuePair<TFirstKey, Dictionary<TSecondKey, TValue>> outerDictionary in this.GFIterator()) {
                foreach (KeyValuePair<TSecondKey, TValue> innerKeyPair in outerDictionary.Value.GFIterator()) {
                    if (predicate(innerKeyPair.Value)) {
                        toRemoveBufferFirstKey.Add(outerDictionary.Key);
                        toRemoveBufferSecondKey.Add(innerKeyPair.Key);
                    }
                }
            }
            
            for (int i = 0; i < toRemoveBufferFirstKey.Count; i++) {
                RemoveInner(toRemoveBufferFirstKey[i], toRemoveBufferSecondKey[i]);
            }
        }
    }
}