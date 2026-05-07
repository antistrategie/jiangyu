namespace TinySerializer.Core.Formatters {
    public static class FormatterEmitter {
        private static int helperFormatterNameId;
        
        public const string PRE_EMITTED_ASSEMBLY_NAME = "OdinSerializer.AOTGenerated";
        
        [EmittedFormatter]
        public abstract class AOTEmittedFormatter<T> : EasyBaseFormatter<T> { }
    }
}