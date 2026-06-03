using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Jiangyu.Core.Reflection;

internal static class AssemblyTypes
{
    /// <summary>
    /// <see cref="Assembly.GetTypes"/> that tolerates a partial load: when some types
    /// fail to resolve it returns the ones that did, rather than throwing. Shared by the
    /// metadata reader and the template catalog so both recover identically.
    /// </summary>
    public static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(type => type != null)!; }
    }
}
