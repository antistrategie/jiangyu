using System;
using System.Collections.Generic;
using System.Reflection;
using TinySerializer.Core.Formatters;
using TinySerializer.Core.Misc;
using TinySerializer.Utilities.Extensions;
using TinySerializer.Utilities.Misc;

namespace TinySerializer.Core.FormatterLocators {
    public static class FormatterLocator {
        private static readonly object StrongFormatters_LOCK = new object();
        private static readonly object WeakFormatters_LOCK = new object();
        
        private static readonly Dictionary<Type, IFormatter> FormatterInstances = new Dictionary<Type, IFormatter>(FastTypeComparer.Instance);
        
        private static readonly DoubleLookupDictionary<Type, ISerializationPolicy, IFormatter> StrongTypeFormatterMap =
            new DoubleLookupDictionary<Type, ISerializationPolicy, IFormatter>(FastTypeComparer.Instance, ReferenceEqualityComparer<ISerializationPolicy>.Default);
        
        private static readonly DoubleLookupDictionary<Type, ISerializationPolicy, IFormatter> WeakTypeFormatterMap =
            new DoubleLookupDictionary<Type, ISerializationPolicy, IFormatter>(FastTypeComparer.Instance, ReferenceEqualityComparer<ISerializationPolicy>.Default);
        
        private struct FormatterInfo {
            public Type FormatterType;
            public Type TargetType;
            public Type WeakFallbackType;
            public bool AskIfCanFormatTypes;
            public int Priority;
        }
        
        private struct FormatterLocatorInfo {
            public IFormatterLocator LocatorInstance;
            public int Priority;
        }
        
        private static readonly List<FormatterLocatorInfo> FormatterLocators = new List<FormatterLocatorInfo>();
        private static readonly List<FormatterInfo> FormatterInfos = new List<FormatterInfo>();
        
        static FormatterLocator() {
            foreach (Assembly ass in AppDomain.CurrentDomain.GetAssemblies()) {
                try {
                    string name = ass.GetName().Name;
                    
                    if (name.StartsWith("System.") || name.StartsWith("UnityEngine") || name.StartsWith("UnityEditor") || name == "mscorlib") {
                        continue;
                    }
                    
                    foreach (object attrUncast in ass.SafeGetCustomAttributes(typeof(RegisterFormatterAttribute), true)) {
                        RegisterFormatterAttribute attr = (RegisterFormatterAttribute)attrUncast;
                        
                        if (!attr.FormatterType.IsClass || attr.FormatterType.IsAbstract || attr.FormatterType.GetConstructor(Type.EmptyTypes) == null
                            || !attr.FormatterType.ImplementsOpenGenericInterface(typeof(IFormatter<>))) {
                            continue;
                        }
                        
                        FormatterInfos.Add(new FormatterInfo() {
                            FormatterType = attr.FormatterType,
                            WeakFallbackType = attr.WeakFallback,
                            TargetType = attr.FormatterType.GetArgumentsOfInheritedOpenGenericInterface(typeof(IFormatter<>))[0],
                            AskIfCanFormatTypes = typeof(IAskIfCanFormatTypes).IsAssignableFrom(attr.FormatterType),
                            Priority = attr.Priority
                        });
                    }
                    
                    foreach (object attrUncast in ass.SafeGetCustomAttributes(typeof(RegisterFormatterLocatorAttribute), true)) {
                        RegisterFormatterLocatorAttribute attr = (RegisterFormatterLocatorAttribute)attrUncast;
                        
                        if (!attr.FormatterLocatorType.IsClass || attr.FormatterLocatorType.IsAbstract || attr.FormatterLocatorType.GetConstructor(Type.EmptyTypes) == null
                            || !typeof(IFormatterLocator).IsAssignableFrom(attr.FormatterLocatorType)) {
                            continue;
                        }
                        
                        try {
                            FormatterLocators.Add(new FormatterLocatorInfo() {
                                LocatorInstance = (IFormatterLocator)Activator.CreateInstance(attr.FormatterLocatorType),
                                Priority = attr.Priority
                            });
                        } catch (Exception ex) {
                            Console.WriteLine(new Exception("Exception was thrown while instantiating FormatterLocator of type " + attr.FormatterLocatorType.FullName + ".", ex));
                        }
                    }
                } catch (TypeLoadException) {
                    if (ass.GetName().Name == "OdinSerializer") {
                        Console.WriteLine("A TypeLoadException occurred when FormatterLocator tried to load types from assembly '" + ass.FullName
                                          + "'. No serialization formatters in this assembly will be found. Serialization will be utterly broken.");
                    }
                } catch (ReflectionTypeLoadException) {
                    if (ass.GetName().Name == "OdinSerializer") {
                        Console.WriteLine("A ReflectionTypeLoadException occurred when FormatterLocator tried to load types from assembly '" + ass.FullName
                                          + "'. No serialization formatters in this assembly will be found. Serialization will be utterly broken.");
                    }
                } catch (MissingMemberException) {
                    if (ass.GetName().Name == "OdinSerializer") {
                        Console.WriteLine("A ReflectionTypeLoadException occurred when FormatterLocator tried to load types from assembly '" + ass.FullName
                                          + "'. No serialization formatters in this assembly will be found. Serialization will be utterly broken.");
                    }
                }
            }
            
            FormatterInfos.Sort((a, b) =>
            {
                int compare = -a.Priority.CompareTo(b.Priority);
                
                if (compare == 0) {
                    compare = a.FormatterType.Name.CompareTo(b.FormatterType.Name);
                }
                
                return compare;
            });
            
            FormatterLocators.Sort((a, b) =>
            {
                int compare = -a.Priority.CompareTo(b.Priority);
                
                if (compare == 0) {
                    compare = a.LocatorInstance.GetType().Name.CompareTo(b.LocatorInstance.GetType().Name);
                }
                
                return compare;
            });
        }
        
        public static IFormatter<T> GetFormatter<T>(ISerializationPolicy policy) {
            return (IFormatter<T>)GetFormatter(typeof(T), policy, false);
        }
        
        public static IFormatter GetFormatter(Type type, ISerializationPolicy policy) {
            return GetFormatter(type, policy, true);
        }
        
        public static IFormatter GetFormatter(Type type, ISerializationPolicy policy, bool allowWeakFallbackFormatters) {
            IFormatter result;
            
            if (type == null) {
                throw new ArgumentNullException("type");
            }
            
            if (policy == null) {
                policy = SerializationPolicies.Strict;
            }
            
            object lockObj = allowWeakFallbackFormatters ? WeakFormatters_LOCK : StrongFormatters_LOCK;
            DoubleLookupDictionary<Type, ISerializationPolicy, IFormatter> formatterMap = allowWeakFallbackFormatters ? WeakTypeFormatterMap : StrongTypeFormatterMap;
            
            lock (lockObj) {
                if (formatterMap.TryGetInnerValue(type, policy, out result) == false) {
                #pragma warning disable 618
                    try {
                        result = CreateFormatter(type, policy, allowWeakFallbackFormatters);
                    } catch (TargetInvocationException ex) {
                        if (ex.GetBaseException() is ExecutionEngineException) {
                            LogAOTError(type, ex.GetBaseException() as ExecutionEngineException);
                        } else {
                            throw ex;
                        }
                    } catch (TypeInitializationException ex) {
                        if (ex.GetBaseException() is ExecutionEngineException) {
                            LogAOTError(type, ex.GetBaseException() as ExecutionEngineException);
                        } else {
                            throw ex;
                        }
                    } catch (ExecutionEngineException ex) {
                        LogAOTError(type, ex);
                    }
                    
                    formatterMap.AddInner(type, policy, result);
                #pragma warning restore 618
                }
            }
            
            return result;
        }
        
        private static void LogAOTError(Type type, Exception ex) {
            string[] types = new List<string>(GetAllPossibleMissingAOTTypes(type)).ToArray();
            
            Console.WriteLine("Creating a serialization formatter for the type '" + type.GetNiceFullName() + "' failed due to missing AOT support. \n\n"
                              + " Please use Odin's AOT generation feature to generate an AOT dll before building, and MAKE SURE that all of the following "
                              + "types were automatically added to the supported types list after a scan (if they were not, please REPORT AN ISSUE with the details of which exact types the scan is missing "
                              + "and ADD THEM MANUALLY): \n\n" + string.Join("\n", types)
                              + "\n\nIF ALL THE TYPES ARE IN THE SUPPORT LIST AND YOU STILL GET THIS ERROR, PLEASE REPORT AN ISSUE."
                              + "The exception contained the following message: \n" + ex.Message);
            
            throw new SerializationAbortException("AOT formatter support was missing for type '" + type.GetNiceFullName() + "'.", ex);
        }
        
        private static IEnumerable<string> GetAllPossibleMissingAOTTypes(Type type) {
            yield return type.GetNiceFullName() + " (name string: '" + TwoWaySerializationBinder.Default.BindToName(type) + "')";
            
            if (!type.IsGenericType) yield break;
            
            foreach (Type arg in type.GetGenericArguments()) {
                yield return arg.GetNiceFullName() + " (name string: '" + TwoWaySerializationBinder.Default.BindToName(arg) + "')";
                
                if (arg.IsGenericType) {
                    foreach (string subArg in GetAllPossibleMissingAOTTypes(arg)) {
                        yield return subArg;
                    }
                }
            }
        }
        
        private static IFormatter CreateFormatter(Type type, ISerializationPolicy policy, bool allowWeakFormatters) {
            if (FormatterUtilities.IsPrimitiveType(type)) {
                throw new ArgumentException("Cannot create formatters for a primitive type like " + type.Name);
            }
            
            for (int i = 0; i < FormatterLocators.Count; i++) {
                try {
                    IFormatter result;
                    
                    if (FormatterLocators[i].LocatorInstance.TryGetFormatter(type, FormatterLocationStep.BeforeRegisteredFormatters, policy, allowWeakFormatters, out result)) {
                        return result;
                    }
                } catch (TargetInvocationException ex) {
                    throw ex;
                } catch (TypeInitializationException ex) {
                    throw ex;
                }
            #pragma warning disable CS0618
                catch (ExecutionEngineException ex)
            #pragma warning restore CS0618
                {
                    throw ex;
                } catch (Exception ex) {
                    Console.WriteLine(new Exception("Exception was thrown while calling FormatterLocator " + FormatterLocators[i].GetType().FullName + ".", ex));
                }
            }
            
            for (int i = 0; i < FormatterInfos.Count; i++) {
                FormatterInfo info = FormatterInfos[i];
                
                Type formatterType = null;
                Type weakFallbackType = null;
                Type[] genericFormatterArgs = null;
                
                if (type == info.TargetType) {
                    formatterType = info.FormatterType;
                } else if (info.FormatterType.IsGenericType && info.TargetType.IsGenericParameter) {
                    Type[] inferredArgs;
                    
                    if (info.FormatterType.TryInferGenericParameters(out inferredArgs, type)) {
                        genericFormatterArgs = inferredArgs;
                    }
                } else if (type.IsGenericType && info.FormatterType.IsGenericType && info.TargetType.IsGenericType
                    && type.GetGenericTypeDefinition() == info.TargetType.GetGenericTypeDefinition()) {
                    Type[] args = type.GetGenericArguments();
                    
                    if (info.FormatterType.AreGenericConstraintsSatisfiedBy(args)) {
                        genericFormatterArgs = args;
                    }
                }
                
                if (formatterType == null && genericFormatterArgs != null) {
                    formatterType = info.FormatterType.GetGenericTypeDefinition().MakeGenericType(genericFormatterArgs);
                    weakFallbackType = info.WeakFallbackType;
                }
                
                if (formatterType != null) {
                    IFormatter instance = null;
                    
                    bool aotError = false;
                    Exception aotEx = null;
                    
                    try {
                        instance = GetFormatterInstance(formatterType);
                    }
                #pragma warning disable 618
                    catch (TargetInvocationException ex) {
                        aotError = true;
                        aotEx = ex;
                    } catch (TypeInitializationException ex) {
                        aotError = true;
                        aotEx = ex;
                    } catch (ExecutionEngineException ex) {
                        aotError = true;
                        aotEx = ex;
                    }
                #pragma warning restore 618
                    
                    if (aotError && allowWeakFormatters) {
                        if (weakFallbackType != null) {
                            instance = (IFormatter)Activator.CreateInstance(weakFallbackType, type);
                        }
                        
                        if (instance == null) {
                            string argsStr = "";
                            
                            for (int j = 0; j < genericFormatterArgs.Length; j++) {
                                if (j > 0) argsStr = argsStr + ", ";
                                argsStr = argsStr + genericFormatterArgs[j].GetNiceFullName();
                            }
                            
                            Console.WriteLine("No AOT support was generated for serialization formatter type '" + info.FormatterType.GetNiceFullName()
                                              + "' for the generic arguments <" + argsStr + ">, and no weak fallback formatter was specified.");
                            
                            throw aotEx;
                        }
                    }
                    
                    if (instance == null) continue;
                    
                    if (info.AskIfCanFormatTypes && !((IAskIfCanFormatTypes)instance).CanFormatType(type)) {
                        continue;
                    }
                    
                    return instance;
                }
            }
            
            for (int i = 0; i < FormatterLocators.Count; i++) {
                try {
                    IFormatter result;
                    
                    if (FormatterLocators[i].LocatorInstance.TryGetFormatter(type, FormatterLocationStep.AfterRegisteredFormatters, policy, allowWeakFormatters, out result)) {
                        return result;
                    }
                } catch (TargetInvocationException ex) {
                    throw ex;
                } catch (TypeInitializationException ex) {
                    throw ex;
                }
            #pragma warning disable CS0618
                catch (ExecutionEngineException ex)
            #pragma warning restore CS0618
                {
                    throw ex;
                } catch (Exception ex) {
                    Console.WriteLine(new Exception("Exception was thrown while calling FormatterLocator " + FormatterLocators[i].GetType().FullName + ".", ex));
                }
            }
            
            try {
                return (IFormatter)Activator.CreateInstance(typeof(ReflectionFormatter<>).MakeGenericType(type));
            } catch (TargetInvocationException ex) {
                if (allowWeakFormatters) return new WeakReflectionFormatter(type);
                throw ex;
            } catch (TypeInitializationException ex) {
                if (allowWeakFormatters) return new WeakReflectionFormatter(type);
                throw ex;
            }
        #pragma warning disable CS0618
            catch (ExecutionEngineException ex)
        #pragma warning restore CS0618
            {
                if (allowWeakFormatters) return new WeakReflectionFormatter(type);
                throw ex;
            }
        }
        
        private static IFormatter GetFormatterInstance(Type type) {
            IFormatter formatter;
            
            if (!FormatterInstances.TryGetValue(type, out formatter)) {
                try {
                    formatter = (IFormatter)Activator.CreateInstance(type);
                    FormatterInstances.Add(type, formatter);
                } catch (TargetInvocationException ex) {
                    throw ex;
                } catch (TypeInitializationException ex) {
                    throw ex;
                }
            #pragma warning disable CS0618
                catch (ExecutionEngineException ex)
            #pragma warning restore CS0618
                {
                    throw ex;
                } catch (Exception ex) {
                    Console.WriteLine(new Exception("Exception was thrown while instantiating formatter '" + type.GetNiceFullName() + "'.", ex));
                }
            }
            
            return formatter;
        }
    }
}