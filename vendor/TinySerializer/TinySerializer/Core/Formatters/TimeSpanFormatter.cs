using System;
using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Formatters;
using TinySerializer.Core.Misc;

[assembly: RegisterFormatter(typeof(TimeSpanFormatter))]

namespace TinySerializer.Core.Formatters {
    public sealed class TimeSpanFormatter : MinimalBaseFormatter<TimeSpan> {
        protected override void Read(ref TimeSpan value, IDataReader reader) {
            string name;
            
            if (reader.PeekEntry(out name) == EntryType.Integer) {
                long ticks;
                reader.ReadInt64(out ticks);
                value = new TimeSpan(ticks);
            }
        }
        
        protected override void Write(ref TimeSpan value, IDataWriter writer) {
            writer.WriteInt64(null, value.Ticks);
        }
    }
}