using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using TinySerializer.Utilities;
using TinySerializer.Utilities.Extensions;
using TinySerializer.Utilities.Misc;

namespace TinySerializer.Core.Misc {
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class BindTypeNameToTypeAttribute : Attribute {
        internal readonly Type NewType;
        internal readonly string OldTypeName;
        
        public BindTypeNameToTypeAttribute(string oldFullTypeName, Type newType) {
            OldTypeName = oldFullTypeName;
            NewType = newType;
        }
    }
    
    public class DefaultSerializationBinder : TwoWaySerializationBinder {
        private static readonly object ASSEMBLY_LOOKUP_LOCK = new object();
        private static readonly Dictionary<string, Assembly> assemblyNameLookUp = new Dictionary<string, Assembly>();
        private static readonly Dictionary<string, Type> customTypeNameToTypeBindings = new Dictionary<string, Type>();
        
        private static readonly object TYPETONAME_LOCK = new object();
        private static readonly Dictionary<Type, string> nameMap = new Dictionary<Type, string>(FastTypeComparer.Instance);
        
        private static readonly object NAMETOTYPE_LOCK = new object();
        private static readonly Dictionary<string, Type> typeMap = new Dictionary<string, Type>();
        
        private static readonly object ASSEMBLY_REGISTER_QUEUE_LOCK = new object();
        private static readonly List<Assembly> assembliesQueuedForRegister = new List<Assembly>();
        private static readonly List<AssemblyLoadEventArgs> assemblyLoadEventsQueuedForRegister = new List<AssemblyLoadEventArgs>();
        
        static DefaultSerializationBinder() {
            AppDomain.CurrentDomain.AssemblyLoad += (sender, args) =>
            {
                lock (ASSEMBLY_REGISTER_QUEUE_LOCK) {
                    assemblyLoadEventsQueuedForRegister.Add(args);
                }
            };
            
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()) {
                lock (ASSEMBLY_REGISTER_QUEUE_LOCK) {
                    assembliesQueuedForRegister.Add(assembly);
                }
            }
            
            lock (ASSEMBLY_LOOKUP_LOCK) {
                customTypeNameToTypeBindings["System.Reflection.MonoMethod"] = typeof(MethodInfo);
                customTypeNameToTypeBindings["System.Reflection.MonoMethod, mscorlib"] = typeof(MethodInfo);
            }
        }
        
        private static void RegisterAllQueuedAssembliesRepeating() {
            while (RegisterQueuedAssemblies()) { }
            
            while (RegisterQueuedAssemblyLoadEvents()) { }
        }
        
        private static bool RegisterQueuedAssemblies() {
            Assembly[] toRegister = null;
            
            lock (ASSEMBLY_REGISTER_QUEUE_LOCK) {
                if (assembliesQueuedForRegister.Count > 0) {
                    toRegister = assembliesQueuedForRegister.ToArray();
                    assembliesQueuedForRegister.Clear();
                }
            }
            
            if (toRegister == null) return false;
            
            for (int i = 0; i < toRegister.Length; i++) {
                RegisterAssembly(toRegister[i]);
            }
            
            return true;
        }
        
        private static bool RegisterQueuedAssemblyLoadEvents() {
            AssemblyLoadEventArgs[] toRegister = null;
            
            lock (ASSEMBLY_REGISTER_QUEUE_LOCK) {
                if (assemblyLoadEventsQueuedForRegister.Count > 0) {
                    toRegister = assemblyLoadEventsQueuedForRegister.ToArray();
                    assemblyLoadEventsQueuedForRegister.Clear();
                }
            }
            
            if (toRegister == null) return false;
            
            for (int i = 0; i < toRegister.Length; i++) {
                AssemblyLoadEventArgs args = toRegister[i];
                Assembly assembly;
                
                try {
                    assembly = args.LoadedAssembly;
                } catch { continue; }
                
                RegisterAssembly(assembly);
            }
            
            return true;
        }
        
        private static void RegisterAssembly(Assembly assembly) {
            string name;
            
            try {
                name = assembly.GetName().Name;
            } catch { return; }
            
            bool wasAdded = false;
            
            lock (ASSEMBLY_LOOKUP_LOCK) {
                if (!assemblyNameLookUp.ContainsKey(name)) {
                    assemblyNameLookUp.Add(name, assembly);
                    wasAdded = true;
                }
            }
            
            if (wasAdded) {
                try {
                    object[] customAttributes = assembly.SafeGetCustomAttributes(typeof(BindTypeNameToTypeAttribute), false);
                    
                    if (customAttributes != null) {
                        for (int i = 0; i < customAttributes.Length; i++) {
                            BindTypeNameToTypeAttribute attr = customAttributes[i] as BindTypeNameToTypeAttribute;
                            
                            if (attr != null && attr.NewType != null) {
                                lock (ASSEMBLY_LOOKUP_LOCK) {
                                    customTypeNameToTypeBindings[attr.OldTypeName] = attr.NewType;
                                    
                                }
                            }
                        }
                    }
                } catch { }
            }
        }
        
        public override string BindToName(Type type, DebugContext debugContext = null) {
            if (type == null) {
                throw new ArgumentNullException("type");
            }
            
            string result;
            
            lock (TYPETONAME_LOCK) {
                if (nameMap.TryGetValue(type, out result) == false) {
                    if (type.IsGenericType) {
                        List<Type> toResolve = type.GetGenericArguments().ToList();
                        HashSet<Assembly> assemblies = new HashSet<Assembly>();
                        
                        while (toResolve.Count > 0) {
                            Type t = toResolve[0];
                            
                            if (t.IsGenericType) {
                                toResolve.AddRange(t.GetGenericArguments());
                            }
                            
                            assemblies.Add(t.Assembly);
                            toResolve.RemoveAt(0);
                        }
                        
                        result = type.FullName + ", " + type.Assembly.GetName().Name;
                        
                        foreach (Assembly ass in assemblies) {
                            result = result.Replace(ass.FullName, ass.GetName().Name);
                        }
                    } else if (type.IsDefined(typeof(CompilerGeneratedAttribute), false)) {
                        result = type.FullName + ", " + type.Assembly.GetName().Name;
                    } else {
                        result = type.FullName + ", " + type.Assembly.GetName().Name;
                    }
                    
                    nameMap.Add(type, result);
                }
            }
            
            return result;
        }
        
        public override bool ContainsType(string typeName) {
            lock (NAMETOTYPE_LOCK) {
                return typeMap.ContainsKey(typeName);
            }
        }
        
        public override Type BindToType(string typeName, DebugContext debugContext = null) {
            if (typeName == null) {
                throw new ArgumentNullException("typeName");
            }
            
            RegisterAllQueuedAssembliesRepeating();
            
            Type result;
            
            lock (NAMETOTYPE_LOCK) {
                if (typeMap.TryGetValue(typeName, out result) == false) {
                    result = ParseTypeName(typeName, debugContext);
                    
                    if (result == null && debugContext != null) {
                        debugContext.LogWarning("Failed deserialization type lookup for type name '" + typeName + "'.");
                    }
                    
                    typeMap.Add(typeName, result);
                }
            }
            
            return result;
        }
        
        private Type ParseTypeName(string typeName, DebugContext debugContext) {
            Type type;
            
            lock (ASSEMBLY_LOOKUP_LOCK) {
                if (customTypeNameToTypeBindings.TryGetValue(typeName, out type)) {
                    return type;
                }
            }
            
            type = Type.GetType(typeName);
            if (type != null) return type;
            
            type = ParseGenericAndOrArrayType(typeName, debugContext);
            if (type != null) return type;
            
            string typeStr, assemblyStr;
            
            ParseName(typeName, out typeStr, out assemblyStr);
            
            if (!string.IsNullOrEmpty(typeStr)) {
                lock (ASSEMBLY_LOOKUP_LOCK) {
                    if (customTypeNameToTypeBindings.TryGetValue(typeStr, out type)) {
                        return type;
                    }
                }
                
                Assembly assembly;
                
                if (assemblyStr != null) {
                    lock (ASSEMBLY_LOOKUP_LOCK) {
                        assemblyNameLookUp.TryGetValue(assemblyStr, out assembly);
                    }
                    
                    if (assembly == null) {
                        try {
                            assembly = Assembly.Load(assemblyStr);
                        } catch { }
                    }
                    
                    if (assembly != null) {
                        try {
                            type = assembly.GetType(typeStr);
                        } catch { }
                        
                        if (type != null) return type;
                    }
                }
                
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                
                for (int i = 0; i < assemblies.Length; i++) {
                    assembly = assemblies[i];
                    
                    try {
                        type = assembly.GetType(typeStr, false);
                    } catch { }
                    
                    if (type != null) return type;
                }
            }
            
            return null;
        }
        
        private static void ParseName(string fullName, out string typeName, out string assemblyName) {
            typeName = null;
            assemblyName = null;
            
            int firstComma = fullName.IndexOf(',');
            
            if (firstComma < 0 || (firstComma + 1) == fullName.Length) {
                typeName = fullName.Trim(',', ' ');
                return;
            } else {
                typeName = fullName.Substring(0, firstComma);
            }
            
            int secondComma = fullName.IndexOf(',', firstComma + 1);
            
            if (secondComma < 0) {
                assemblyName = fullName.Substring(firstComma).Trim(',', ' ');
            } else {
                assemblyName = fullName.Substring(firstComma, secondComma - firstComma).Trim(',', ' ');
            }
        }
        
        private Type ParseGenericAndOrArrayType(string typeName, DebugContext debugContext) {
            string actualTypeName;
            List<string> genericArgNames;
            
            bool isGeneric;
            bool isArray;
            int arrayRank;
            
            if (!TryParseGenericAndOrArrayTypeName(typeName, out actualTypeName, out isGeneric, out genericArgNames, out isArray, out arrayRank)) return null;
            
            Type type = BindToType(actualTypeName, debugContext);
            
            if (type == null) return null;
            
            if (isGeneric) {
                if (!type.IsGenericType) return null;
                
                using (Cache<List<Type>> argsCache = Cache<List<Type>>.Claim()) {
                    List<Type> args = argsCache.Value;
                    args.Clear();
                    
                    for (int i = 0; i < genericArgNames.Count; i++) {
                        Type arg = BindToType(genericArgNames[i], debugContext);
                        if (arg == null) return null;
                        args.Add(arg);
                    }
                    
                    Type[] argsArray = args.ToArray();
                    
                    if (!type.AreGenericConstraintsSatisfiedBy(argsArray)) {
                        if (debugContext != null) {
                            string argsStr = "";
                            
                            foreach (Type arg in argsArray) {
                                if (argsStr != "") argsStr += ", ";
                                argsStr += arg.GetNiceFullName();
                            }
                            
                            debugContext.LogWarning("Deserialization type lookup failure: The generic type arguments '" + argsStr
                                                    + "' do not satisfy the generic constraints of generic type definition '" + type.GetNiceFullName()
                                                    + "'. All this parsed from the full type name string: '" + typeName + "'");
                        }
                        
                        return null;
                    }
                    
                    type = type.MakeGenericType(argsArray);
                    args.Clear();
                }
            }
            
            if (isArray) {
                if (arrayRank == 1) {
                    type = type.MakeArrayType();
                } else {
                    type = type.MakeArrayType(arrayRank);
                }
            }
            
            return type;
        }
        
        private static bool TryParseGenericAndOrArrayTypeName(string typeName, out string actualTypeName, out bool isGeneric, out List<string> genericArgNames, out bool isArray,
                                                              out int arrayRank) {
            isGeneric = false;
            isArray = false;
            arrayRank = 0;
            
            bool parsingGenericArguments = false;
            
            string argName;
            genericArgNames = null;
            actualTypeName = null;
            
            for (int i = 0; i < typeName.Length; i++) {
                if (typeName[i] == '[') {
                    char next = Peek(typeName, i, 1);
                    
                    if (next == ',' || next == ']') {
                        if (actualTypeName == null) {
                            actualTypeName = typeName.Substring(0, i);
                        }
                        
                        isArray = true;
                        arrayRank = 1;
                        i++;
                        
                        if (next == ',') {
                            while (next == ',') {
                                arrayRank++;
                                next = Peek(typeName, i, 1);
                                i++;
                            }
                            
                            if (next != ']')
                                return false;
                        }
                    } else {
                        if (!isGeneric) {
                            actualTypeName = typeName.Substring(0, i);
                            isGeneric = true;
                            parsingGenericArguments = true;
                            genericArgNames = new List<string>();
                        } else if (isGeneric && ReadGenericArg(typeName, ref i, out argName)) {
                            genericArgNames.Add(argName);
                        } else return false;
                    }
                } else if (typeName[i] == ']') {
                    if (!parsingGenericArguments)
                        return
                            false;
                    
                    parsingGenericArguments = false;
                } else if (typeName[i] == ',' && !parsingGenericArguments) {
                    actualTypeName += typeName.Substring(i);
                    break;
                }
            }
            
            return isArray || isGeneric;
        }
        
        private static char Peek(string str, int i, int ahead) {
            if (i + ahead < str.Length) return str[i + ahead];
            return '\0';
        }
        
        private static bool ReadGenericArg(string typeName, ref int i, out string argName) {
            argName = null;
            if (typeName[i] != '[') return false;
            
            int start = i + 1;
            int genericDepth = 0;
            
            for (; i < typeName.Length; i++) {
                if (typeName[i] == '[') genericDepth++;
                else if (typeName[i] == ']') {
                    genericDepth--;
                    
                    if (genericDepth == 0) {
                        int length = i - start;
                        argName = typeName.Substring(start, length);
                        return true;
                    }
                }
            }
            
            return false;
        }
    }
}