using TinySerializer.Core.DataReaderWriters;

namespace TinySerializer.Core.Misc {
    public interface ISelfFormatter {
        void Serialize(IDataWriter writer);
        
        void Deserialize(IDataReader reader);
    }
}