using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using TinySerializer.Utilities.Misc;

namespace TinySerializer.Utilities.Extensions {
    public static class TypeExtensions {
        private static readonly object GenericConstraintsSatisfaction_LOCK = new object();
        private static readonly Dictionary<Type, Type> GenericConstraintsSatisfactionInferredParameters = new Dictionary<Type, Type>();
        private static readonly Dictionary<Type, Type> GenericConstraintsSatisfactionResolvedMap = new Dictionary<Type, Type>();
        private static readonly HashSet<Type> GenericConstraintsSatisfactionProcessedParams = new HashSet<Type>();
        private static readonly HashSet<Type> GenericConstraintsSatisfactionTypesToCheck = new HashSet<Type>();
        private static readonly List<Type> GenericConstraintsSatisfactionTypesToCheck_ToAdd = new List<Type>();
        
        private static readonly Type GenericListInterface = typeof(IList<>);
        private static readonly Type GenericCollectionInterface = typeof(ICollection<>);
        
        private static readonly object WeaklyTypedTypeCastDelegates_LOCK = new object();
        
        
        private static readonly DoubleLookupDictionary<Type, Type, Func<object, object>> WeaklyTypedTypeCastDelegates =
            new DoubleLookupDictionary<Type, Type, Func<object, object>>();
        
        public static readonly Dictionary<string, string> TypeNameAlternatives = new Dictionary<string, string>() {
            { "Single", "float" },
            { "Double", "double" },
            { "SByte", "sbyte" },
            { "Int16", "short" },
            { "Int32", "int" },
            { "Int64", "long" },
            { "Byte", "byte" },
            { "UInt16", "ushort" },
            { "UInt32", "uint" },
            { "UInt64", "ulong" },
            { "Decimal", "decimal" },
            { "String", "string" },
            { "Char", "char" },
            { "Boolean", "bool" },
            { "Single[]", "float[]" },
            { "Double[]", "double[]" },
            { "SByte[]", "sbyte[]" },
            { "Int16[]", "short[]" },
            { "Int32[]", "int[]" },
            { "Int64[]", "long[]" },
            { "Byte[]", "byte[]" },
            { "UInt16[]", "ushort[]" },
            { "UInt32[]", "uint[]" },
            { "UInt64[]", "ulong[]" },
            { "Decimal[]", "decimal[]" },
            { "String[]", "string[]" },
            { "Char[]", "char[]" },
            { "Boolean[]", "bool[]" },
        };
        
        private static readonly object CachedNiceNames_LOCK = new object();
        private static readonly Dictionary<Type, string> CachedNiceNames = new Dictionary<Type, string>();
        
        private static string GetCachedNiceName(Type type) {
            string result;
            
            lock (CachedNiceNames_LOCK) {
                if (!CachedNiceNames.TryGetValue(type, out result)) {
                    result = CreateNiceName(type);
                    CachedNiceNames.Add(type, result);
                }
            }
            
            return result;
        }
        
        private static string CreateNiceName(Type type) {
            if (type.IsArray) {
                int rank = type.GetArrayRank();
                return type.GetElementType().GetNiceName() + (rank == 1 ? "[]" : "[,]");
            }
            
            if (type.InheritsFrom(typeof(Nullable<>))) {
                return type.GetGenericArguments()[0].GetNiceName() + "?";
            }
            
            if (type.IsByRef) {
                return "ref " + type.GetElementType().GetNiceName();
            }
            
            if (type.IsGenericParameter || !type.IsGenericType) {
                return TypeNameGauntlet(type);
            }
            
            StringBuilder builder = new StringBuilder();
            string name = type.Name;
            int index = name.IndexOf("`");
            
            if (index != -1) {
                builder.Append(name.Substring(0, index));
            } else {
                builder.Append(name);
            }
            
            builder.Append('<');
            Type[] args = type.GetGenericArguments();
            
            for (int i = 0; i < args.Length; i++) {
                Type arg = args[i];
                
                if (i != 0) {
                    builder.Append(", ");
                }
                
                builder.Append(GetNiceName(arg));
            }
            
            builder.Append('>');
            return builder.ToString();
        }
        
        private static readonly Type VoidPointerType = typeof(void).MakePointerType();
        
        private static readonly Dictionary<Type, HashSet<Type>> PrimitiveImplicitCasts = new Dictionary<Type, HashSet<Type>>() {
            { typeof(Int64), new HashSet<Type>() { typeof(Single), typeof(Double), typeof(Decimal) } },
            { typeof(Int32), new HashSet<Type>() { typeof(Int64), typeof(Single), typeof(Double), typeof(Decimal) } },
            { typeof(Int16), new HashSet<Type>() { typeof(Int32), typeof(Int64), typeof(Single), typeof(Double), typeof(Decimal) } },
            { typeof(SByte), new HashSet<Type>() { typeof(Int16), typeof(Int32), typeof(Int64), typeof(Single), typeof(Double), typeof(Decimal) } },
            { typeof(UInt64), new HashSet<Type>() { typeof(Single), typeof(Double), typeof(Decimal) } },
            { typeof(UInt32), new HashSet<Type>() { typeof(Int64), typeof(UInt64), typeof(Single), typeof(Double), typeof(Decimal) } },
            { typeof(UInt16), new HashSet<Type>() { typeof(Int32), typeof(UInt32), typeof(Int64), typeof(UInt64), typeof(Single), typeof(Double), typeof(Decimal) } }, {
                typeof(Byte),
                new HashSet<Type>() { typeof(Int16), typeof(UInt16), typeof(Int32), typeof(UInt32), typeof(Int64), typeof(UInt64), typeof(Single), typeof(Double), typeof(Decimal) }
            },
            { typeof(Char), new HashSet<Type>() { typeof(UInt16), typeof(Int32), typeof(UInt32), typeof(Int64), typeof(UInt64), typeof(Single), typeof(Double), typeof(Decimal) } },
            { typeof(Boolean), new HashSet<Type>() { } },
            { typeof(Decimal), new HashSet<Type>() { } },
            { typeof(Single), new HashSet<Type>() { typeof(Double) } },
            { typeof(Double), new HashSet<Type>() { } },
            { typeof(IntPtr), new HashSet<Type>() { } },
            { typeof(UIntPtr), new HashSet<Type>() { } },
            { VoidPointerType, new HashSet<Type>() { } },
        };
        
        private static readonly HashSet<Type> ExplicitCastIntegrals = new HashSet<Type>() {
            { typeof(Int64) },
            { typeof(Int32) },
            { typeof(Int16) },
            { typeof(SByte) },
            { typeof(UInt64) },
            { typeof(UInt32) },
            { typeof(UInt16) },
            { typeof(Byte) },
            { typeof(Char) },
            { typeof(Decimal) },
            { typeof(Single) },
            { typeof(Double) },
            { typeof(IntPtr) },
            { typeof(UIntPtr) }
        };
        
        internal static bool HasCastDefined(this Type from, Type to, bool requireImplicitCast) {
            if (from.IsEnum) {
                return Enum.GetUnderlyingType(from).IsCastableTo(to);
            }
            
            if (to.IsEnum) {
                return Enum.GetUnderlyingType(to).IsCastableTo(from);
            }
            
            if ((from.IsPrimitive || from == VoidPointerType) && (to.IsPrimitive || to == VoidPointerType)) {
                if (requireImplicitCast) {
                    return PrimitiveImplicitCasts[from].Contains(to);
                } else {
                    if (from == typeof(IntPtr)) {
                        if (to == typeof(UIntPtr)) {
                            return false;
                        } else if (to == VoidPointerType) {
                            return true;
                        }
                    } else if (from == typeof(UIntPtr)) {
                        if (to == typeof(IntPtr)) {
                            return false;
                        } else if (to == VoidPointerType) {
                            return true;
                        }
                    }
                    
                    return ExplicitCastIntegrals.Contains(from) && ExplicitCastIntegrals.Contains(to);
                }
            }
            
            return from.GetCastMethod(to, requireImplicitCast) != null;
        }
        
        public static bool IsCastableTo(this Type from, Type to, bool requireImplicitCast = false) {
            if (from == null) {
                throw new ArgumentNullException("from");
            }
            
            if (to == null) {
                throw new ArgumentNullException("to");
            }
            
            if (from == to) {
                return true;
            }
            
            return to.IsAssignableFrom(from) || from.HasCastDefined(to, requireImplicitCast);
        }
        
        public static Func<object, object> GetCastMethodDelegate(this Type from, Type to, bool requireImplicitCast = false) {
            Func<object, object> result;
            
            lock (WeaklyTypedTypeCastDelegates_LOCK) {
                if (WeaklyTypedTypeCastDelegates.TryGetInnerValue(from, to, out result) == false) {
                    MethodInfo method = GetCastMethod(from, to, requireImplicitCast);
                    
                    if (method != null) {
                        result = (obj) => method.Invoke(null, new object[] { obj });
                    }
                    
                    WeaklyTypedTypeCastDelegates.AddInner(from, to, result);
                }
            }
            
            return result;
        }
        
        public static MethodInfo GetCastMethod(this Type from, Type to, bool requireImplicitCast = false) {
            IEnumerable<MethodInfo> fromMethods = from.GetAllMembers<MethodInfo>(BindingFlags.Public | BindingFlags.Static);
            
            foreach (MethodInfo method in fromMethods) {
                if ((method.Name == "op_Implicit" || (requireImplicitCast == false && method.Name == "op_Explicit"))
                    && method.GetParameters()[0].ParameterType.IsAssignableFrom(from) && to.IsAssignableFrom(method.ReturnType)) {
                    return method;
                }
            }
            
            IEnumerable<MethodInfo> toMethods = to.GetAllMembers<MethodInfo>(BindingFlags.Public | BindingFlags.Static);
            
            foreach (MethodInfo method in toMethods) {
                if ((method.Name == "op_Implicit" || (requireImplicitCast == false && method.Name == "op_Explicit"))
                    && method.GetParameters()[0].ParameterType.IsAssignableFrom(from) && to.IsAssignableFrom(method.ReturnType)) {
                    return method;
                }
            }
            
            return null;
        }
        
        public static bool ImplementsOrInherits(this Type type, Type to) {
            return to.IsAssignableFrom(type);
        }
        
        public static bool ImplementsOpenGenericInterface(this Type candidateType, Type openGenericInterfaceType) {
            if (candidateType == openGenericInterfaceType)
                return true;
            
            if (candidateType.IsGenericType && candidateType.GetGenericTypeDefinition() == openGenericInterfaceType)
                return true;
            
            Type[] interfaces = candidateType.GetInterfaces();
            
            for (int i = 0; i < interfaces.Length; i++) {
                if (interfaces[i].ImplementsOpenGenericInterface(openGenericInterfaceType))
                    return true;
            }
            
            return false;
        }
        
        public static bool ImplementsOpenGenericClass(this Type candidateType, Type openGenericType) {
            if (candidateType.IsGenericType && candidateType.GetGenericTypeDefinition() == openGenericType)
                return true;
            
            Type baseType = candidateType.BaseType;
            
            if (baseType != null && baseType.ImplementsOpenGenericClass(openGenericType))
                return true;
            
            return false;
        }
        
        public static Type[] GetArgumentsOfInheritedOpenGenericClass(this Type candidateType, Type openGenericType) {
            if (candidateType.IsGenericType && candidateType.GetGenericTypeDefinition() == openGenericType)
                return candidateType.GetGenericArguments();
            
            Type baseType = candidateType.BaseType;
            
            if (baseType != null)
                return baseType.GetArgumentsOfInheritedOpenGenericClass(openGenericType);
            
            return null;
        }
        
        public static Type[] GetArgumentsOfInheritedOpenGenericInterface(this Type candidateType, Type openGenericInterfaceType) {
            if ((openGenericInterfaceType == GenericListInterface || openGenericInterfaceType == GenericCollectionInterface) && candidateType.IsArray) {
                return new Type[] { candidateType.GetElementType() };
            }
            
            if (candidateType == openGenericInterfaceType)
                return candidateType.GetGenericArguments();
            
            if (candidateType.IsGenericType && candidateType.GetGenericTypeDefinition() == openGenericInterfaceType)
                return candidateType.GetGenericArguments();
            
            Type[] interfaces = candidateType.GetInterfaces();
            
            for (int i = 0; i < interfaces.Length; i++) {
                Type @interface = interfaces[i];
                if (!@interface.IsGenericType) continue;
                
                Type[] result = @interface.GetArgumentsOfInheritedOpenGenericInterface(openGenericInterfaceType);
                
                if (result != null)
                    return result;
            }
            
            return null;
        }
        
        public static IEnumerable<T> GetAllMembers<T>(this Type type, BindingFlags flags = BindingFlags.Default) where T : MemberInfo {
            if (type == null) throw new ArgumentNullException("type");
            if (type == typeof(object)) yield break;
            
            Type currentType = type;
            
            if ((flags & BindingFlags.DeclaredOnly) == BindingFlags.DeclaredOnly) {
                foreach (MemberInfo member in currentType.GetMembers(flags)) {
                    T found = member as T;
                    
                    if (found != null) {
                        yield return found;
                    }
                }
            } else {
                flags |= BindingFlags.DeclaredOnly;
                
                do {
                    foreach (MemberInfo member in currentType.GetMembers(flags)) {
                        T found = member as T;
                        
                        if (found != null) {
                            yield return found;
                        }
                    }
                    
                    currentType = currentType.BaseType;
                } while (currentType != null);
            }
        }
        
        private static string TypeNameGauntlet(this Type type) {
            string typeName = type.Name;
            
            string altTypeName = string.Empty;
            
            if (TypeNameAlternatives.TryGetValue(typeName, out altTypeName)) {
                typeName = altTypeName;
            }
            
            return typeName;
        }
        
        public static string GetNiceName(this Type type) {
            if (type.IsNested && type.IsGenericParameter == false) {
                return type.DeclaringType.GetNiceName() + "." + GetCachedNiceName(type);
            }
            
            return GetCachedNiceName(type);
        }
        
        public static string GetNiceFullName(this Type type) {
            string result;
            
            if (type.IsNested && type.IsGenericParameter == false) {
                return type.DeclaringType.GetNiceFullName() + "." + GetCachedNiceName(type);
            }
            
            result = GetCachedNiceName(type);
            
            if (type.Namespace != null) {
                result = type.Namespace + "." + result;
            }
            
            return result;
        }
        
        public static bool IsDefined<T>(this Type type) where T : Attribute {
            return type.IsDefined(typeof(T), false);
        }
        
        public static bool InheritsFrom(this Type type, Type baseType) {
            if (baseType.IsAssignableFrom(type)) {
                return true;
            }
            
            if (type.IsInterface && baseType.IsInterface == false) {
                return false;
            }
            
            if (baseType.IsInterface) {
                return type.GetInterfaces().Contains(baseType);
            }
            
            Type t = type;
            
            while (t != null) {
                if (t == baseType) {
                    return true;
                }
                
                if (baseType.IsGenericTypeDefinition && t.IsGenericType && t.GetGenericTypeDefinition() == baseType) {
                    return true;
                }
                
                t = t.BaseType;
            }
            
            return false;
        }
        
        public static bool TryInferGenericParameters(this Type genericTypeDefinition, out Type[] inferredParams, params Type[] knownParameters) {
            if (genericTypeDefinition == null) {
                throw new ArgumentNullException("genericTypeDefinition");
            }
            
            if (knownParameters == null) {
                throw new ArgumentNullException("knownParameters");
            }
            
            if (!genericTypeDefinition.IsGenericType) {
                throw new ArgumentException("The genericTypeDefinition parameter must be a generic type.");
            }
            
            lock (GenericConstraintsSatisfaction_LOCK) {
                Dictionary<Type, Type> matches = GenericConstraintsSatisfactionInferredParameters;
                matches.Clear();
                
                HashSet<Type> typesToCheck = GenericConstraintsSatisfactionTypesToCheck;
                typesToCheck.Clear();
                
                List<Type> typesToCheck_ToAdd = GenericConstraintsSatisfactionTypesToCheck_ToAdd;
                typesToCheck_ToAdd.Clear();
                
                for (int i = 0; i < knownParameters.Length; i++) {
                    typesToCheck.Add(knownParameters[i]);
                }
                
                Type[] definitions = genericTypeDefinition.GetGenericArguments();
                
                if (!genericTypeDefinition.IsGenericTypeDefinition) {
                    Type[] constructedParameters = definitions;
                    genericTypeDefinition = genericTypeDefinition.GetGenericTypeDefinition();
                    definitions = genericTypeDefinition.GetGenericArguments();
                    
                    int unknownCount = 0;
                    
                    for (int i = 0; i < constructedParameters.Length; i++) {
                        if (!constructedParameters[i].IsGenericParameter && (!constructedParameters[i].IsGenericType || constructedParameters[i].IsFullyConstructedGenericType())) {
                            matches[definitions[i]] = constructedParameters[i];
                        } else {
                            unknownCount++;
                        }
                    }
                    
                    if (unknownCount == knownParameters.Length) {
                        int count = 0;
                        
                        for (int i = 0; i < constructedParameters.Length; i++) {
                            if (constructedParameters[i].IsGenericParameter) {
                                constructedParameters[i] = knownParameters[count++];
                            }
                        }
                        
                        if (genericTypeDefinition.AreGenericConstraintsSatisfiedBy(constructedParameters)) {
                            inferredParams = constructedParameters;
                            return true;
                        }
                    }
                }
                
                if (definitions.Length == knownParameters.Length && genericTypeDefinition.AreGenericConstraintsSatisfiedBy(knownParameters)) {
                    inferredParams = knownParameters;
                    return true;
                }
                
                foreach (Type typeArg in definitions) {
                    
                    Type[] constraints = typeArg.GetGenericParameterConstraints();
                    
                    foreach (Type constraint in constraints) {
                        foreach (Type parameter in typesToCheck) {
                            if (!constraint.IsGenericType) {
                                continue;
                            }
                            
                            Type constraintDefinition = constraint.GetGenericTypeDefinition();
                            
                            Type[] constraintParams = constraint.GetGenericArguments();
                            Type[] paramParams;
                            
                            if (parameter.IsGenericType && constraintDefinition == parameter.GetGenericTypeDefinition()) {
                                paramParams = parameter.GetGenericArguments();
                            } else if (constraintDefinition.IsInterface && parameter.ImplementsOpenGenericInterface(constraintDefinition)) {
                                paramParams = parameter.GetArgumentsOfInheritedOpenGenericInterface(constraintDefinition);
                            } else if (constraintDefinition.IsClass && parameter.ImplementsOpenGenericClass(constraintDefinition)) {
                                paramParams = parameter.GetArgumentsOfInheritedOpenGenericClass(constraintDefinition);
                            } else {
                                continue;
                            }
                            
                            matches[typeArg] = parameter;
                            typesToCheck_ToAdd.Add(parameter);
                            
                            for (int i = 0; i < constraintParams.Length; i++) {
                                if (constraintParams[i].IsGenericParameter) {
                                    matches[constraintParams[i]] = paramParams[i];
                                    typesToCheck_ToAdd.Add(paramParams[i]);
                                }
                            }
                        }
                        
                        foreach (Type type in typesToCheck_ToAdd) {
                            typesToCheck.Add(type);
                        }
                        
                        typesToCheck_ToAdd.Clear();
                    }
                }
                
                if (matches.Count == definitions.Length) {
                    inferredParams = new Type[matches.Count];
                    
                    for (int i = 0; i < definitions.Length; i++) {
                        inferredParams[i] = matches[definitions[i]];
                    }
                    
                    if (AreGenericConstraintsSatisfiedBy(genericTypeDefinition, inferredParams)) {
                        return true;
                    }
                }
                
                inferredParams = null;
                return false;
            }
        }
        
        public static bool AreGenericConstraintsSatisfiedBy(this Type genericType, params Type[] parameters) {
            if (genericType == null) {
                throw new ArgumentNullException("genericType");
            }
            
            if (parameters == null) {
                throw new ArgumentNullException("parameters");
            }
            
            if (!genericType.IsGenericType) {
                throw new ArgumentException("The genericTypeDefinition parameter must be a generic type.");
            }
            
            return AreGenericConstraintsSatisfiedBy(genericType.GetGenericArguments(), parameters);
        }
        
        public static bool AreGenericConstraintsSatisfiedBy(Type[] definitions, Type[] parameters) {
            if (definitions.Length != parameters.Length) {
                return false;
            }
            
            lock (GenericConstraintsSatisfaction_LOCK) {
                Dictionary<Type, Type> resolvedMap = GenericConstraintsSatisfactionResolvedMap;
                resolvedMap.Clear();
                
                for (int i = 0; i < definitions.Length; i++) {
                    Type definition = definitions[i];
                    Type parameter = parameters[i];
                    
                    if (!definition.GenericParameterIsFulfilledBy(parameter, resolvedMap)) {
                        return false;
                    }
                }
                
                return true;
            }
        }
        
        private static bool GenericParameterIsFulfilledBy(this Type genericParameterDefinition, Type parameterType, Dictionary<Type, Type> resolvedMap,
                                                          HashSet<Type> processedParams = null) {
            if (genericParameterDefinition == null) {
                throw new ArgumentNullException("genericParameterDefinition");
            }
            
            if (parameterType == null) {
                throw new ArgumentNullException("parameterType");
            }
            
            if (resolvedMap == null) {
                throw new ArgumentNullException("resolvedMap");
            }
            
            if (genericParameterDefinition.IsGenericParameter == false && genericParameterDefinition == parameterType) {
                return true;
            }
            
            if (genericParameterDefinition.IsGenericParameter == false) {
                return false;
            }
            
            if (processedParams == null) {
                processedParams = GenericConstraintsSatisfactionProcessedParams;
                processedParams.Clear();
            }
            
            processedParams.Add(genericParameterDefinition);
            
            GenericParameterAttributes specialConstraints = genericParameterDefinition.GenericParameterAttributes;
            
            if (specialConstraints != GenericParameterAttributes.None) {
                if ((specialConstraints & GenericParameterAttributes.NotNullableValueTypeConstraint) == GenericParameterAttributes.NotNullableValueTypeConstraint) {
                    if (!parameterType.IsValueType || (parameterType.IsGenericType && parameterType.GetGenericTypeDefinition() == typeof(Nullable<>))) {
                        return false;
                    }
                } else if ((specialConstraints & GenericParameterAttributes.ReferenceTypeConstraint) == GenericParameterAttributes.ReferenceTypeConstraint) {
                    if (parameterType.IsValueType) {
                        return false;
                    }
                }
                
                if ((specialConstraints & GenericParameterAttributes.DefaultConstructorConstraint) == GenericParameterAttributes.DefaultConstructorConstraint) {
                    if (parameterType.IsAbstract || (!parameterType.IsValueType && parameterType.GetConstructor(Type.EmptyTypes) == null)) {
                        return false;
                    }
                }
            }
            
            if (resolvedMap.ContainsKey(genericParameterDefinition)) {
                if (!parameterType.IsAssignableFrom(resolvedMap[genericParameterDefinition])) {
                    return false;
                }
            }
            
            Type[] constraints = genericParameterDefinition.GetGenericParameterConstraints();
            
            for (int i = 0; i < constraints.Length; i++) {
                Type constraint = constraints[i];
                
                if (constraint.IsGenericParameter && resolvedMap.ContainsKey(constraint)) {
                    constraint = resolvedMap[constraint];
                }
                
                if (constraint.IsGenericParameter) {
                    if (!constraint.GenericParameterIsFulfilledBy(parameterType, resolvedMap, processedParams)) {
                        return false;
                    }
                } else if (constraint.IsClass || constraint.IsInterface || constraint.IsValueType) {
                    if (constraint.IsGenericType) {
                        Type constraintDefinition = constraint.GetGenericTypeDefinition();
                        
                        Type[] constraintParams = constraint.GetGenericArguments();
                        Type[] paramParams;
                        
                        if (parameterType.IsGenericType && constraintDefinition == parameterType.GetGenericTypeDefinition()) {
                            paramParams = parameterType.GetGenericArguments();
                        } else {
                            if (constraintDefinition.IsClass) {
                                if (parameterType.ImplementsOpenGenericClass(constraintDefinition)) {
                                    paramParams = parameterType.GetArgumentsOfInheritedOpenGenericClass(constraintDefinition);
                                } else {
                                    return false;
                                }
                            } else {
                                if (parameterType.ImplementsOpenGenericInterface(constraintDefinition)) {
                                    paramParams = parameterType.GetArgumentsOfInheritedOpenGenericInterface(constraintDefinition);
                                } else {
                                    return false;
                                }
                            }
                        }
                        
                        for (int j = 0; j < constraintParams.Length; j++) {
                            Type c = constraintParams[j];
                            Type p = paramParams[j];
                            
                            if (c.IsGenericParameter && resolvedMap.ContainsKey(c)) {
                                c = resolvedMap[c];
                            }
                            
                            if (c.IsGenericParameter) {
                                if (!processedParams.Contains(c) && !GenericParameterIsFulfilledBy(c, p, resolvedMap, processedParams)) {
                                    return false;
                                }
                            } else if (c != p && !c.IsAssignableFrom(p)) {
                                return false;
                            }
                        }
                    } else if (!constraint.IsAssignableFrom(parameterType)) {
                        return false;
                    }
                } else {
                    throw new Exception("Unknown parameter constraint type! " + constraint.GetNiceName());
                }
            }
            
            resolvedMap[genericParameterDefinition] = parameterType;
            return true;
        }
        
        public static bool IsFullyConstructedGenericType(this Type type) {
            if (type == null) {
                throw new ArgumentNullException("type");
            }
            
            if (type.IsGenericTypeDefinition) {
                return false;
            }
            
            if (type.HasElementType) {
                Type element = type.GetElementType();
                
                if (element.IsGenericParameter || element.IsFullyConstructedGenericType() == false) {
                    return false;
                }
            }
            
            Type[] args = type.GetGenericArguments();
            
            for (int i = 0; i < args.Length; i++) {
                Type arg = args[i];
                
                if (arg.IsGenericParameter) {
                    return false;
                } else if (!arg.IsFullyConstructedGenericType()) {
                    return false;
                }
            }
            
            return !type.IsGenericTypeDefinition;
        }
        
        public static object[] SafeGetCustomAttributes(this Assembly assembly, Type type, bool inherit) {
            try {
                return assembly.GetCustomAttributes(type, inherit);
            } catch {
                return new object[0];
            }
        }
    }
}