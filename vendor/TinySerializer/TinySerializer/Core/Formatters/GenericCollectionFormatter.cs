using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Misc;
using TinySerializer.Core.Serializers;
using TinySerializer.Utilities.Extensions;

namespace TinySerializer.Core.Formatters {
    public static class GenericCollectionFormatter {
        public static bool CanFormat(Type type, out Type elementType) {
            if (type == null) {
                throw new ArgumentNullException();
            }
            
            if (type.IsAbstract || type.IsGenericTypeDefinition || type.IsInterface || type.GetConstructor(Type.EmptyTypes) == null
                || type.ImplementsOpenGenericInterface(typeof(ICollection<>)) == false) {
                elementType = null;
                return false;
            }
            
            elementType = type.GetArgumentsOfInheritedOpenGenericInterface(typeof(ICollection<>))[0];
            return true;
        }
    }
    
    public sealed class GenericCollectionFormatter<TCollection, TElement> : BaseFormatter<TCollection> where TCollection : ICollection<TElement>, new() {
        private static Serializer<TElement> valueReaderWriter = Serializer.Get<TElement>();
        
        static GenericCollectionFormatter() {
            Type e;
            
            if (GenericCollectionFormatter.CanFormat(typeof(TCollection), out e) == false) {
                throw new ArgumentException("Cannot treat the type " + typeof(TCollection).Name + " as a generic collection.");
            }
            
            if (e != typeof(TElement)) {
                throw new ArgumentException("Type " + typeof(TElement).Name + " is not the element type of the generic collection type " + typeof(TCollection).Name + ".");
            }
            
            new GenericCollectionFormatter<List<int>, int>();
        }
        
        public GenericCollectionFormatter() { }
        
        protected override TCollection GetUninitializedObject() {
            return new TCollection();
        }
        
        protected override void DeserializeImplementation(ref TCollection value, IDataReader reader) {
            string name;
            EntryType entry = reader.PeekEntry(out name);
            
            if (entry == EntryType.StartOfArray) {
                try {
                    long length;
                    reader.EnterArray(out length);
                    
                    for (int i = 0; i < length; i++) {
                        if (reader.PeekEntry(out name) == EntryType.EndOfArray) {
                            reader.Context.Config.DebugContext.LogError("Reached end of array after " + i + " elements, when " + length + " elements were expected.");
                            break;
                        }
                        
                        try {
                            value.Add(valueReaderWriter.ReadValue(reader));
                        } catch (Exception ex) {
                            reader.Context.Config.DebugContext.LogException(ex);
                        }
                        
                        if (reader.IsInArrayNode == false) {
                            reader.Context.Config.DebugContext.LogError("Reading array went wrong. Data dump: " + reader.GetDataDump());
                            break;
                        }
                    }
                } catch (Exception ex) {
                    reader.Context.Config.DebugContext.LogException(ex);
                } finally {
                    reader.ExitArray();
                }
            } else {
                reader.SkipEntry();
            }
        }
        
        protected override void SerializeImplementation(ref TCollection value, IDataWriter writer) {
            try {
                writer.BeginArrayNode(value.Count);
                
                foreach (TElement element in value) {
                    valueReaderWriter.WriteValue(element, writer);
                }
            } finally {
                writer.EndArrayNode();
            }
        }
    }
    
    public sealed class WeakGenericCollectionFormatter : WeakBaseFormatter {
        private readonly Serializer ValueReaderWriter;
        private readonly Type ElementType;
        private readonly PropertyInfo CountProperty;
        private readonly MethodInfo AddMethod;
        
        public WeakGenericCollectionFormatter(Type collectionType, Type elementType) : base(collectionType) {
            ElementType = elementType;
            CountProperty = collectionType.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            AddMethod = collectionType.GetMethod("Add", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic, null, new Type[] { elementType }, null);
            
            if (AddMethod == null) {
                throw new ArgumentException("Cannot treat the type " + collectionType.Name + " as a generic collection since it has no accessible Add method.");
            }
            
            if (CountProperty == null || CountProperty.PropertyType != typeof(int)) {
                throw new ArgumentException("Cannot treat the type " + collectionType.Name + " as a generic collection since it has no accessible Count property.");
            }
            
            Type e;
            
            if (GenericCollectionFormatter.CanFormat(collectionType, out e) == false) {
                throw new ArgumentException("Cannot treat the type " + collectionType.Name + " as a generic collection.");
            }
            
            if (e != elementType) {
                throw new ArgumentException("Type " + elementType.Name + " is not the element type of the generic collection type " + collectionType.Name + ".");
            }
        }
        
        protected override object GetUninitializedObject() {
            return Activator.CreateInstance(SerializedType);
        }
        
        protected override void DeserializeImplementation(ref object value, IDataReader reader) {
            string name;
            EntryType entry = reader.PeekEntry(out name);
            
            if (entry == EntryType.StartOfArray) {
                try {
                    long length;
                    reader.EnterArray(out length);
                    
                    for (int i = 0; i < length; i++) {
                        if (reader.PeekEntry(out name) == EntryType.EndOfArray) {
                            reader.Context.Config.DebugContext.LogError("Reached end of array after " + i + " elements, when " + length + " elements were expected.");
                            break;
                        }
                        
                        object[] addParams = new object[1];
                        
                        try {
                            addParams[0] = ValueReaderWriter.ReadValueWeak(reader);
                            AddMethod.Invoke(value, addParams);
                        } catch (Exception ex) {
                            reader.Context.Config.DebugContext.LogException(ex);
                        }
                        
                        if (reader.IsInArrayNode == false) {
                            reader.Context.Config.DebugContext.LogError("Reading array went wrong. Data dump: " + reader.GetDataDump());
                            break;
                        }
                    }
                } catch (Exception ex) {
                    reader.Context.Config.DebugContext.LogException(ex);
                } finally {
                    reader.ExitArray();
                }
            } else {
                reader.SkipEntry();
            }
        }
        
        protected override void SerializeImplementation(ref object value, IDataWriter writer) {
            try {
                writer.BeginArrayNode((int)CountProperty.GetValue(value, null));
                
                foreach (object element in (IEnumerable)value) {
                    ValueReaderWriter.WriteValueWeak(element, writer);
                }
            } finally {
                writer.EndArrayNode();
            }
        }
    }
}