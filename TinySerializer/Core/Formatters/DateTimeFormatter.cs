using System;
using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Formatters;
using TinySerializer.Core.Misc;

[assembly: RegisterFormatter(typeof(DateTimeFormatter))]

namespace TinySerializer.Core.Formatters {
    public sealed class DateTimeFormatter : MinimalBaseFormatter<DateTime> {
        protected override void Read(ref DateTime value, IDataReader reader) {
            string name;
            
            if (reader.PeekEntry(out name) == EntryType.Integer) {
                long binary;
                reader.ReadInt64(out binary);
                value = DateTime.FromBinary(binary);
            }
        }
        
        protected override void Write(ref DateTime value, IDataWriter writer) {
            writer.WriteInt64(null, value.ToBinary());
        }
    }
}