using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Jiangyu.Loader.Sdk;

/// <summary>Reflection helpers shared by the SDK runtime.</summary>
internal static class ModAssemblyScan
{
    /// <summary>Returns the assembly's loadable types, tolerating a partial type-load failure.</summary>
    public static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null)!;
        }
    }
}
