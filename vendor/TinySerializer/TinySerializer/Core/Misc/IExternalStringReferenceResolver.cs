namespace TinySerializer.Core.Misc {
    public interface IExternalStringReferenceResolver {
        IExternalStringReferenceResolver NextResolver { get; set; }
        
        bool TryResolveReference(string id, out object value);
        
        bool CanReference(object value, out string id);
    }
}