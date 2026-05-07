using System;
using System.Collections;
using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Formatters;
using TinySerializer.Core.Misc;
using TinySerializer.Core.Serializers;

[assembly: RegisterFormatter(typeof(ArrayListFormatter))]

namespace TinySerializer.Core.Formatters {
    public class ArrayListFormatter : BaseFormatter<ArrayList> {
        private static readonly Serializer<object> ObjectSerializer = Serializer.Get<object>();
        
        protected override ArrayList GetUninitializedObject() {
            return null;
        }
        
        protected override void DeserializeImplementation(ref ArrayList value, IDataReader reader) {
            string name;
            EntryType entry = reader.PeekEntry(out name);
            
            if (entry == EntryType.StartOfArray) {
                try {
                    long length;
                    reader.EnterArray(out length);
                    value = new ArrayList((int)length);
                    
                    RegisterReferenceID(value, reader);
                    
                    for (int i = 0; i < length; i++) {
                        if (reader.PeekEntry(out name) == EntryType.EndOfArray) {
                            reader.Context.Config.DebugContext.LogError("Reached end of array after " + i + " elements, when " + length + " elements were expected.");
                            break;
                        }
                        
                        value.Add(ObjectSerializer.ReadValue(reader));
                        
                        if (reader.IsInArrayNode == false) {
                            reader.Context.Config.DebugContext.LogError("Reading array went wrong. Data dump: " + reader.GetDataDump());
                            break;
                        }
                    }
                } finally {
                    reader.ExitArray();
                }
            } else {
                reader.SkipEntry();
            }
        }
        
        protected override void SerializeImplementation(ref ArrayList value, IDataWriter writer) {
            try {
                writer.BeginArrayNode(value.Count);
                
                for (int i = 0; i < value.Count; i++) {
                    try {
                        ObjectSerializer.WriteValue(value[i], writer);
                    } catch (Exception ex) {
                        writer.Context.Config.DebugContext.LogException(ex);
                    }
                }
            } finally {
                writer.EndArrayNode();
            }
        }
    }
}