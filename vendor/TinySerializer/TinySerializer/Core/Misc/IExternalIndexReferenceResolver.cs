namespace TinySerializer.Core.Misc {
    public interface IExternalIndexReferenceResolver {
        bool TryResolveReference(int index, out object value);
        
        bool CanReference(object value, out int index);
    }
}