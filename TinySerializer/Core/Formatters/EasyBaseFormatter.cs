using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Misc;

namespace TinySerializer.Core.Formatters {
    public abstract class EasyBaseFormatter<T> : BaseFormatter<T> {
        protected sealed override void DeserializeImplementation(ref T value, IDataReader reader) {
            int count = 0;
            string name;
            EntryType entry;
            
            while ((entry = reader.PeekEntry(out name)) != EntryType.EndOfNode && entry != EntryType.EndOfArray && entry != EntryType.EndOfStream) {
                ReadDataEntry(ref value, name, entry, reader);
                
                count++;
                
                if (count > 1000) {
                    reader.Context.Config.DebugContext.LogError("Breaking out of infinite reading loop!");
                    break;
                }
            }
        }
        
        protected sealed override void SerializeImplementation(ref T value, IDataWriter writer) {
            WriteDataEntries(ref value, writer);
        }
        
        protected abstract void ReadDataEntry(ref T value, string entryName, EntryType entryType, IDataReader reader);
        
        protected abstract void WriteDataEntries(ref T value, IDataWriter writer);
    }
}