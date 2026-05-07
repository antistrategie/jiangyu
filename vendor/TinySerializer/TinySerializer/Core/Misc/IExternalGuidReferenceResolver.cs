using System;

namespace TinySerializer.Core.Misc {
    public interface IExternalGuidReferenceResolver {
        IExternalGuidReferenceResolver NextResolver { get; set; }
        
        bool TryResolveReference(Guid guid, out object value);
        
        bool CanReference(object value, out Guid guid);
    }
}