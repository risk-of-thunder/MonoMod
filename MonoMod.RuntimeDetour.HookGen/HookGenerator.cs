using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Utils;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace MonoMod.RuntimeDetour.HookGen {
    public class HookGenerator {

        const string ObsoleteMessageBackCompat = "This method only exists for backwards-compatibility purposes.";

        static readonly Regex NameVerifyRegex = new Regex("[^a-zA-Z]"); // Don't set RegexOptions.Compiled as old versions of mono hate it.

        static readonly Dictionary<Type, string> ReflTypeNameMap = new Dictionary<Type, string>() {
            { typeof(string), "string" },
            { typeof(object), "object" },
            { typeof(bool), "bool" },
            { typeof(byte), "byte" },
            { typeof(char), "char" },
            { typeof(decimal), "decimal" },
            { typeof(double), "double" },
            { typeof(short), "short" },
            { typeof(int), "int" },
            { typeof(long), "long" },
            { typeof(sbyte), "sbyte" },
            { typeof(float), "float" },
            { typeof(ushort), "ushort" },
            { typeof(uint), "uint" },
            { typeof(ulong), "ulong" },
            { typeof(void), "void" }
        };
        static readonly Dictionary<string, string> TypeNameMap = new Dictionary<string, string>();

        static HookGenerator() {
            foreach (KeyValuePair<Type, string> pair in ReflTypeNameMap)
                TypeNameMap[pair.Key.FullName] = pair.Value;
        }

        public MonoModder Modder;

        public ModuleDefinition OutputModule;

        public string Namespace;
        public string NamespaceIL;
        public bool HookOrig;
        public bool HookPrivate;

        public bool NoVisibleCheck;
        public bool NoVisibleCheckBackCompatSuffix;
        // Backcompat experiment.
        //public const string NoVisibleCheckBackCompatSuffixString = "_PT";
        public const string NoVisibleCheckBackCompatSuffixString = "";

        public string HookExtName;

        public ModuleDefinition module_RuntimeDetour;
        public ModuleDefinition module_Utils;

        public TypeReference t_MulticastDelegate;
        public TypeReference t_IAsyncResult;
        public TypeReference t_AsyncCallback;
        public TypeReference t_MethodBase;
        public TypeReference t_RuntimeMethodHandle;
        public TypeReference t_EditorBrowsableState;

        public MethodReference m_Object_ctor;
        public MethodReference m_ObsoleteAttribute_ctor;
        public MethodReference m_EditorBrowsableAttribute_ctor;

        public MethodReference m_GetMethodFromHandle;
        public MethodReference m_Add;
        public MethodReference m_Remove;
        public MethodReference m_Modify;
        public MethodReference m_Unmodify;

        public TypeReference t_ILManipulator;

        public HookGenerator(MonoModder modder, string name) {
            Modder = modder;

            OutputModule = ModuleDefinition.CreateModule(name, new ModuleParameters {
                Architecture = modder.Module.Architecture,
                AssemblyResolver = modder.Module.AssemblyResolver,
                Kind = ModuleKind.Dll,
                Runtime = modder.Module.Runtime
            });

            // Copy all assembly references from the input module.
            // Cecil + .NET Standard libraries + .NET 5.0 = weirdness.
            modder.MapDependencies();
            OutputModule.AssemblyReferences.AddRange(modder.Module.AssemblyReferences);
            modder.DependencyMap[OutputModule] = new List<ModuleDefinition>(modder.DependencyMap[modder.Module]);

            Namespace = Environment.GetEnvironmentVariable("MONOMOD_HOOKGEN_NAMESPACE");
            if (string.IsNullOrEmpty(Namespace))
                Namespace = "On";
            NamespaceIL = Environment.GetEnvironmentVariable("MONOMOD_HOOKGEN_NAMESPACE_IL");
            if (string.IsNullOrEmpty(NamespaceIL))
                NamespaceIL = "IL";
            HookOrig = Environment.GetEnvironmentVariable("MONOMOD_HOOKGEN_ORIG") == "1";
            HookPrivate = Environment.GetEnvironmentVariable("MONOMOD_HOOKGEN_PRIVATE") == "1";
            NoVisibleCheck = Environment.GetEnvironmentVariable("MONOMOD_HOOKGEN_NO_VISIBLE_CHECK") == "1";
            NoVisibleCheckBackCompatSuffix = Environment.GetEnvironmentVariable("MONOMOD_HOOKGEN_NO_VISIBLE_CHECK_BACKCOMPAT_SUFFIX") == "1";

            modder.MapDependency(modder.Module, "MonoMod.RuntimeDetour");
            if (!modder.DependencyCache.TryGetValue("MonoMod.RuntimeDetour", out module_RuntimeDetour))
                throw new FileNotFoundException("MonoMod.RuntimeDetour not found!");

            modder.MapDependency(modder.Module, "MonoMod.Utils");
            if (!modder.DependencyCache.TryGetValue("MonoMod.Utils", out module_Utils))
                throw new FileNotFoundException("MonoMod.Utils not found!");

            t_MulticastDelegate = OutputModule.ImportReference(modder.FindType("System.MulticastDelegate"));
            t_IAsyncResult = OutputModule.ImportReference(modder.FindType("System.IAsyncResult"));
            t_AsyncCallback = OutputModule.ImportReference(modder.FindType("System.AsyncCallback"));
            t_MethodBase = OutputModule.ImportReference(modder.FindType("System.Reflection.MethodBase"));
            t_RuntimeMethodHandle = OutputModule.ImportReference(modder.FindType("System.RuntimeMethodHandle"));
            t_EditorBrowsableState = OutputModule.ImportReference(modder.FindType("System.ComponentModel.EditorBrowsableState"));

            TypeDefinition td_HookEndpointManager = module_RuntimeDetour.GetType("MonoMod.RuntimeDetour.HookGen.HookEndpointManager");

            t_ILManipulator = OutputModule.ImportReference(
                module_Utils.GetType("MonoMod.Cil.ILContext/Manipulator")
            );

            m_Object_ctor = OutputModule.ImportReference(modder.FindType("System.Object").Resolve().FindMethod("System.Void .ctor()"));
            m_ObsoleteAttribute_ctor = OutputModule.ImportReference(modder.FindType("System.ObsoleteAttribute").Resolve().FindMethod("System.Void .ctor(System.String,System.Boolean)"));
            m_EditorBrowsableAttribute_ctor = OutputModule.ImportReference(modder.FindType("System.ComponentModel.EditorBrowsableAttribute").Resolve().FindMethod("System.Void .ctor(System.ComponentModel.EditorBrowsableState)"));

            m_GetMethodFromHandle = OutputModule.ImportReference(
                new MethodReference("GetMethodFromHandle", t_MethodBase, t_MethodBase) {
                    Parameters = {
                        new ParameterDefinition(t_RuntimeMethodHandle)
                    }
                }
            );
            m_Add = OutputModule.ImportReference(td_HookEndpointManager.FindMethod("Add"));
            m_Remove = OutputModule.ImportReference(td_HookEndpointManager.FindMethod("Remove"));
            m_Modify = OutputModule.ImportReference(td_HookEndpointManager.FindMethod("Modify"));
            m_Unmodify = OutputModule.ImportReference(td_HookEndpointManager.FindMethod("Unmodify"));

        }

        public void Generate() {
            foreach (TypeDefinition type in Modder.Module.Types) {
                GenerateFor(type, out TypeDefinition hookType, out TypeDefinition hookILType);
                if (hookType == null || hookILType == null || hookType.IsNested)
                    continue;
                OutputModule.Types.Add(hookType);
                OutputModule.Types.Add(hookILType);
            }
        }

        public void GenerateFor(TypeDefinition type, out TypeDefinition hookType, out TypeDefinition hookILType) {
            hookType = hookILType = null;

            if (type.HasGenericParameters ||
                type.IsRuntimeSpecialName ||
                type.Name.StartsWith("<", StringComparison.Ordinal))
                return;

            if (!HookPrivate && type.IsNotPublic)
                return;

            Modder.LogVerbose($"[HookGen] Generating for type {type.FullName}");

            hookType = new TypeDefinition(
                type.IsNested ? null : (Namespace + (string.IsNullOrEmpty(type.Namespace) ? "" : ("." + type.Namespace))),
                type.Name,
                (type.IsNested ? TypeAttributes.NestedPublic : TypeAttributes.Public) |
                TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class,
                OutputModule.TypeSystem.Object
            );

            hookILType = new TypeDefinition(
                type.IsNested ? null : (NamespaceIL + (string.IsNullOrEmpty(type.Namespace) ? "" : ("." + type.Namespace))),
                type.Name,
                (type.IsNested ? TypeAttributes.NestedPublic : TypeAttributes.Public) |
                TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class,
                OutputModule.TypeSystem.Object
            );

            bool add = false;

            foreach (MethodDefinition method in type.Methods)
                add |= GenerateFor(hookType, hookILType, method);

            foreach (TypeDefinition nested in type.NestedTypes) {
                GenerateFor(nested, out TypeDefinition hookNestedType, out TypeDefinition hookNestedILType);
                if (hookNestedType == null || hookNestedILType == null)
                    continue;
                add = true;
                hookType.NestedTypes.Add(hookNestedType);
                hookILType.NestedTypes.Add(hookNestedILType);
            }

            if (!add) {
                hookType = hookILType = null;
            }
        }

        /// <summary>
        /// Find a method for a given ID.
        /// </summary>
        /// <param name="type">The type to search in.</param>
        /// <param name="id">The method ID.</param>
        /// <param name="simple">Whether to perform a simple search pass as well or not.</param>
        /// <returns>The first matching method or null.</returns>
        public static List<MethodDefinition> FindMethods(TypeDefinition type, string id, bool simple = true) {
            List<MethodDefinition> methods = new List<MethodDefinition>();
            if (simple && !id.Contains(" ", StringComparison.Ordinal)) {
                // First simple pass: With type name (just "Namespace.Type::MethodName")
                foreach (MethodDefinition method in type.Methods)
                    if (method.GetID(simple: true) == id)
                        methods.Add(method);
                // Second simple pass: Without type name (basically name only)
                foreach (MethodDefinition method in type.Methods)
                    if (method.GetID(withType: false, simple: true) == id)
                        methods.Add(method);
            }

            // First pass: With type name (f.e. global searches)
            foreach (MethodDefinition method in type.Methods)
                if (method.GetID() == id)
                    methods.Add(method);
            // Second pass: Without type name (f.e. LinkTo)
            foreach (MethodDefinition method in type.Methods)
                if (method.GetID(withType: false) == id)
                    methods.Add(method);

            return methods;
        }

        public bool GenerateFor(TypeDefinition hookType, TypeDefinition hookILType, MethodDefinition method) {
            if (method.HasGenericParameters ||
                method.IsAbstract ||
                (method.IsSpecialName && !method.IsConstructor))
                return false;

            if (!HookOrig && method.Name.StartsWith("orig_", StringComparison.Ordinal))
                return false;
            if (!HookPrivate && method.IsPrivate)
                return false;

            string name = GetFriendlyName(method);
            bool suffix = true;
            if (method.Parameters.Count == 0) {
                suffix = false;
            }

            IEnumerable<MethodDefinition> overloads = null;
            if (suffix) {
                overloads = method.DeclaringType.Methods.Where(other => !other.HasGenericParameters &&
                GetFriendlyName(other) == name && other != method);
                if (overloads.Count() == 0) {
                    suffix = false;
                }
            }

            if (suffix) {
                StringBuilder builder = new StringBuilder();
                for (int parami = 0; parami < method.Parameters.Count; parami++) {
                    ParameterDefinition param = method.Parameters[parami];
                    if (!TypeNameMap.TryGetValue(param.ParameterType.FullName, out string typeName))
                        typeName = GetFriendlyName(param.ParameterType, false);

                    if (overloads.Any(other => {
                        ParameterDefinition otherParam = other.Parameters.ElementAtOrDefault(parami);
                        return
                            otherParam != null &&
                            GetFriendlyName(otherParam.ParameterType, false) == typeName &&
                            otherParam.ParameterType.Namespace != param.ParameterType.Namespace;
                    }))
                        typeName = GetFriendlyName(param.ParameterType, true);

                    builder.Append("_");
                    builder.Append(typeName.Replace(".", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal));
                }
                name += builder.ToString();
            }

            if (hookType.FindEvent(name) != null) {
                string nameTmp;
                for (
                    int i = 1;
                    hookType.FindEvent(nameTmp = name + "_" + i) != null;
                    i++
                )
                    ;
                name = nameTmp;
            }

            // TODO: Fix possible conflict when other members with the same names exist.

            GenerateDelegateForResult delOrigs = GenerateDelegateFor(method, true);
            {
                TypeDefinition delOrig = delOrigs.TypeDef;
                delOrig.Name = "orig_" + name;
                delOrig.CustomAttributes.Add(GenerateEditorBrowsable(EditorBrowsableState.Never));
                hookType.NestedTypes.Add(delOrig);
            }
            if (delOrigs.TypeDefWithNoVisibleCheckSuffix != null) {
                TypeDefinition delOrig = delOrigs.TypeDefWithNoVisibleCheckSuffix;
                delOrig.Name = "orig_" + name + NoVisibleCheckBackCompatSuffixString;
                delOrig.CustomAttributes.Add(GenerateEditorBrowsable(EditorBrowsableState.Never));
                hookType.NestedTypes.Add(delOrig);
            }

            GenerateDelegateForResult delHooks = GenerateDelegateFor(method, false);
            {
                TypeDefinition delOrig = delOrigs.TypeDef;
                TypeDefinition delHook = delHooks.TypeDef;
                delHook.Name = "hook_" + name;
                List<MethodDefinition> delHookInvokes = HookGenerator.FindMethods(delHook, "Invoke");
                foreach (MethodDefinition delHookInvoke in delHookInvokes) {
                    delHookInvoke.Parameters.Insert(0, new ParameterDefinition("orig", ParameterAttributes.None, delOrig));
                }
                List<MethodDefinition> delHookBeginInvokes = HookGenerator.FindMethods(delHook, "BeginInvoke");
                foreach (MethodDefinition delHookBeginInvoke in delHookBeginInvokes) {
                    delHookBeginInvoke.Parameters.Insert(0, new ParameterDefinition("orig", ParameterAttributes.None, delOrig));
                }
                delHook.CustomAttributes.Add(GenerateEditorBrowsable(EditorBrowsableState.Never));
                hookType.NestedTypes.Add(delHook);
            }
            if (delHooks.TypeDefWithNoVisibleCheckSuffix != null && delOrigs.TypeDefWithNoVisibleCheckSuffix != null) {
                TypeDefinition delOrig = delOrigs.TypeDefWithNoVisibleCheckSuffix;
                TypeDefinition delHook = delHooks.TypeDefWithNoVisibleCheckSuffix;
                delHook.Name = "hook_" + name + NoVisibleCheckBackCompatSuffixString;
                List<MethodDefinition> delHookInvokes = HookGenerator.FindMethods(delHook, "Invoke");
                foreach (MethodDefinition delHookInvoke in delHookInvokes) {
                    delHookInvoke.Parameters.Insert(0, new ParameterDefinition("orig", ParameterAttributes.None, delOrig));
                }
                List<MethodDefinition> delHookBeginInvokes = HookGenerator.FindMethods(delHook, "BeginInvoke");
                foreach (MethodDefinition delHookBeginInvoke in delHookBeginInvokes) {
                    delHookBeginInvoke.Parameters.Insert(0, new ParameterDefinition("orig", ParameterAttributes.None, delOrig));
                }
                delHook.CustomAttributes.Add(GenerateEditorBrowsable(EditorBrowsableState.Never));
                hookType.NestedTypes.Add(delHook);
            }

            ILProcessor il;
            GenericInstanceMethod endpointMethod;

            MethodReference methodRef = OutputModule.ImportReference(method);

            if (delHooks.TypeDefWithNoVisibleCheckSuffix != null && delOrigs.TypeDefWithNoVisibleCheckSuffix != null) {
                GenerateFor2(delHooks.TypeDef, true);

                // Backcompat experiment.
                GenerateFor2(delHooks.TypeDefWithNoVisibleCheckSuffix, false);
                //GenerateFor2(delHooks.TypeDefWithNoVisibleCheckSuffix, true);
            } else {
                // Backcompat experiment.
                GenerateFor2(delHooks.TypeDef, false);
                //GenerateFor2(delHooks.TypeDef, true);
            }

            return true;

            void GenerateFor2(TypeDefinition delHook, bool isPrivate) {
                #region Hook

                MethodDefinition addHook = new MethodDefinition(
                    "add_" + name,
                    (isPrivate ? MethodAttributes.Private : MethodAttributes.Public) | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.Static,
                    OutputModule.TypeSystem.Void
                );
                addHook.Parameters.Add(new ParameterDefinition(null, ParameterAttributes.None, delHook));
                addHook.Body = new MethodBody(addHook);
                il = addHook.Body.GetILProcessor();
                il.Emit(OpCodes.Ldtoken, methodRef);
                il.Emit(OpCodes.Call, m_GetMethodFromHandle);
                il.Emit(OpCodes.Ldarg_0);
                endpointMethod = new GenericInstanceMethod(m_Add);
                endpointMethod.GenericArguments.Add(delHook);
                il.Emit(OpCodes.Call, endpointMethod);
                il.Emit(OpCodes.Ret);
                if (isPrivate)
                    addHook.CustomAttributes.Add(GenerateEditorBrowsable(EditorBrowsableState.Never));
                hookType.Methods.Add(addHook);

                MethodDefinition removeHook = new MethodDefinition(
                    "remove_" + name,
                    (isPrivate ? MethodAttributes.Private : MethodAttributes.Public) | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.Static,
                    OutputModule.TypeSystem.Void
                );
                removeHook.Parameters.Add(new ParameterDefinition(null, ParameterAttributes.None, delHook));
                removeHook.Body = new MethodBody(removeHook);
                il = removeHook.Body.GetILProcessor();
                il.Emit(OpCodes.Ldtoken, methodRef);
                il.Emit(OpCodes.Call, m_GetMethodFromHandle);
                il.Emit(OpCodes.Ldarg_0);
                endpointMethod = new GenericInstanceMethod(m_Remove);
                endpointMethod.GenericArguments.Add(delHook);
                il.Emit(OpCodes.Call, endpointMethod);
                il.Emit(OpCodes.Ret);
                if (isPrivate)
                    removeHook.CustomAttributes.Add(GenerateEditorBrowsable(EditorBrowsableState.Never));
                hookType.Methods.Add(removeHook);

                EventDefinition evHook = new EventDefinition(name, EventAttributes.None, delHook) {
                    AddMethod = addHook,
                    RemoveMethod = removeHook
                };
                if (isPrivate)
                    evHook.CustomAttributes.Add(GenerateEditorBrowsable(EditorBrowsableState.Never));
                hookType.Events.Add(evHook);

                #endregion

                #region Hook IL

                MethodDefinition addIL = new MethodDefinition(
                    "add_" + name,
                    (isPrivate ? MethodAttributes.Private : MethodAttributes.Public) | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.Static,
                    OutputModule.TypeSystem.Void
                );
                addIL.Parameters.Add(new ParameterDefinition(null, ParameterAttributes.None, t_ILManipulator));
                addIL.Body = new MethodBody(addIL);
                il = addIL.Body.GetILProcessor();
                il.Emit(OpCodes.Ldtoken, methodRef);
                il.Emit(OpCodes.Call, m_GetMethodFromHandle);
                il.Emit(OpCodes.Ldarg_0);
                endpointMethod = new GenericInstanceMethod(m_Modify);
                endpointMethod.GenericArguments.Add(delHook);
                il.Emit(OpCodes.Call, endpointMethod);
                il.Emit(OpCodes.Ret);
                hookILType.Methods.Add(addIL);

                MethodDefinition removeIL = new MethodDefinition(
                    "remove_" + name,
                    (isPrivate ? MethodAttributes.Private : MethodAttributes.Public) | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.Static,
                    OutputModule.TypeSystem.Void
                );
                removeIL.Parameters.Add(new ParameterDefinition(null, ParameterAttributes.None, t_ILManipulator));
                removeIL.Body = new MethodBody(removeIL);
                il = removeIL.Body.GetILProcessor();
                il.Emit(OpCodes.Ldtoken, methodRef);
                il.Emit(OpCodes.Call, m_GetMethodFromHandle);
                il.Emit(OpCodes.Ldarg_0);
                endpointMethod = new GenericInstanceMethod(m_Unmodify);
                endpointMethod.GenericArguments.Add(delHook);
                il.Emit(OpCodes.Call, endpointMethod);
                il.Emit(OpCodes.Ret);
                hookILType.Methods.Add(removeIL);

                EventDefinition evIL = new EventDefinition(name, EventAttributes.None, t_ILManipulator) {
                    AddMethod = addIL,
                    RemoveMethod = removeIL
                };
                hookILType.Events.Add(evIL);

                #endregion
            }
        }

        public struct GenerateDelegateForResult {
            public TypeDefinition TypeDef;
            public TypeDefinition TypeDefWithNoVisibleCheckSuffix;
        }

        public GenerateDelegateForResult GenerateDelegateFor(MethodDefinition method, bool isOrigDelegate) {
            TypeDefinition del = null;
            TypeDefinition delWithNoVisibleCheckSuffix = null;

            if (NoVisibleCheck) {
                if (NoVisibleCheckBackCompatSuffix) {
                    // Private types allowed, potential NoVisibleCheck suffix.
                    bool needSuffix = GenerateDelegateFor2(method, false, isOrigDelegate, out del);
                    if (needSuffix) {
                        GenerateDelegateFor2(method, true, isOrigDelegate, out delWithNoVisibleCheckSuffix);
                    }
                } else {
                    // Private types allowed, no NoVisibleCheck suffix.
                    GenerateDelegateFor2(method, true, isOrigDelegate, out del);
                }
            } else {
                // No private types allowed, no NoVisibleCheck suffix.
                GenerateDelegateFor2(method, false, isOrigDelegate, out del);
            }

            return new GenerateDelegateForResult {
                TypeDef = del,
                TypeDefWithNoVisibleCheckSuffix = delWithNoVisibleCheckSuffix
            };
        }

        // Returns true if this method should be called again for NoVisibleBackCompat purposes.
        private bool GenerateDelegateFor2(MethodDefinition method, bool allowPrivateTypes, bool isOrigDelegate, out TypeDefinition del) {
            del = new TypeDefinition(
                null, null,
                TypeAttributes.NestedPublic | TypeAttributes.Sealed | TypeAttributes.Class,
                t_MulticastDelegate
            );
            MethodDefinition ctor = new MethodDefinition(
                ".ctor",
                MethodAttributes.Public |
                MethodAttributes.HideBySig |
                MethodAttributes.SpecialName | MethodAttributes.RTSpecialName |
                MethodAttributes.ReuseSlot,
                OutputModule.TypeSystem.Void
            ) {
                ImplAttributes = MethodImplAttributes.Runtime | MethodImplAttributes.Managed,
                HasThis = true
            };
            ctor.Parameters.Add(new ParameterDefinition(OutputModule.TypeSystem.Object));
            ctor.Parameters.Add(new ParameterDefinition(OutputModule.TypeSystem.IntPtr));
            ctor.Body = new MethodBody(ctor);
            del.Methods.Add(ctor);

            bool needVisibilityBackCompat = false;

            TypeReference invokeReturnType = GetImportedTypeAndCheckIfNoVisibleBackCompatSuffixNeeded(method.ReturnType,
                allowPrivateTypes, ref needVisibilityBackCompat);
            MethodDefinition invoke = new MethodDefinition(
                "Invoke",
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                invokeReturnType
            ) {
                ImplAttributes = MethodImplAttributes.Runtime | MethodImplAttributes.Managed,
                HasThis = true
            };
            if (!method.IsStatic) {
                TypeReference selfType = GetImportedTypeAndCheckIfNoVisibleBackCompatSuffixNeeded(method.DeclaringType,
                    allowPrivateTypes, ref needVisibilityBackCompat);
                if (method.DeclaringType.IsValueType)
                    selfType = new ByReferenceType(selfType);
                invoke.Parameters.Add(new ParameterDefinition("self", ParameterAttributes.None, selfType));
            }
            foreach (ParameterDefinition param in method.Parameters) {
                TypeReference paramType = GetImportedTypeAndCheckIfNoVisibleBackCompatSuffixNeeded(param.ParameterType,
                    allowPrivateTypes, ref needVisibilityBackCompat);
                invoke.Parameters.Add(new ParameterDefinition(
                    param.Name,
                    param.Attributes & ~ParameterAttributes.Optional & ~ParameterAttributes.HasDefault,
                    paramType
                ));
            }
            invoke.Body = new MethodBody(invoke);
            del.Methods.Add(invoke);

            MethodDefinition invokeBegin = new MethodDefinition(
                "BeginInvoke",
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                t_IAsyncResult
            ) {
                ImplAttributes = MethodImplAttributes.Runtime | MethodImplAttributes.Managed,
                HasThis = true
            };
            foreach (ParameterDefinition param in invoke.Parameters)
                invokeBegin.Parameters.Add(new ParameterDefinition(param.Name, param.Attributes, param.ParameterType));
            invokeBegin.Parameters.Add(new ParameterDefinition("callback", ParameterAttributes.None, t_AsyncCallback));
            invokeBegin.Parameters.Add(new ParameterDefinition(null, ParameterAttributes.None, OutputModule.TypeSystem.Object));
            invokeBegin.Body = new MethodBody(invokeBegin);
            del.Methods.Add(invokeBegin);

            MethodDefinition invokeEnd = new MethodDefinition(
                "EndInvoke",
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                OutputModule.TypeSystem.Object
            ) {
                ImplAttributes = MethodImplAttributes.Runtime | MethodImplAttributes.Managed,
                HasThis = true
            };
            invokeEnd.Parameters.Add(new ParameterDefinition("result", ParameterAttributes.None, t_IAsyncResult));
            invokeEnd.Body = new MethodBody(invokeEnd);
            del.Methods.Add(invokeEnd);

            // Backcompat experiment.
            if (needVisibilityBackCompat && isOrigDelegate) {
                del.Attributes &= ~TypeAttributes.NestedPublic;
                del.Attributes |= TypeAttributes.NestedPrivate;

                //ctor.Attributes &= ~MethodAttributes.Public;
                //ctor.Attributes |= MethodAttributes.Private;

                //invoke.Attributes &= ~MethodAttributes.Public;
                //invoke.Attributes |= MethodAttributes.Private;

                //invokeBegin.Attributes &= ~MethodAttributes.Public;
                //invokeBegin.Attributes |= MethodAttributes.Private;

                //invokeEnd.Attributes &= ~MethodAttributes.Public;
                //invokeEnd.Attributes |= MethodAttributes.Private;

                // Block needed so that mono is happy and correctly find the Invoke methods
                {
                    bool unused = false;
                    TypeReference invokeReturnType2 = GetImportedTypeAndCheckIfNoVisibleBackCompatSuffixNeeded(method.ReturnType,
    true, ref needVisibilityBackCompat);
                    MethodDefinition invoke2 = new MethodDefinition(
                        "Invoke",
                        MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                        invokeReturnType2
                    ) {
                        ImplAttributes = MethodImplAttributes.Runtime | MethodImplAttributes.Managed,
                        HasThis = true
                    };
                    if (!method.IsStatic) {
                        TypeReference selfType2 = GetImportedTypeAndCheckIfNoVisibleBackCompatSuffixNeeded(method.DeclaringType,
                            true, ref unused);
                        if (method.DeclaringType.IsValueType)
                            selfType2 = new ByReferenceType(selfType2);
                        invoke2.Parameters.Add(new ParameterDefinition("self", ParameterAttributes.None, selfType2));
                    }
                    foreach (ParameterDefinition param in method.Parameters) {
                        TypeReference paramType = GetImportedTypeAndCheckIfNoVisibleBackCompatSuffixNeeded(param.ParameterType,
                            true, ref unused);
                        invoke2.Parameters.Add(new ParameterDefinition(
                            param.Name,
                            param.Attributes & ~ParameterAttributes.Optional & ~ParameterAttributes.HasDefault,
                            paramType
                        ));
                    }
                    invoke2.Body = new MethodBody(invoke2);
                    del.Methods.Add(invoke2);

                    MethodDefinition invokeBegin2 = new MethodDefinition(
    "BeginInvoke",
    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
    t_IAsyncResult
) {
                        ImplAttributes = MethodImplAttributes.Runtime | MethodImplAttributes.Managed,
                        HasThis = true
                    };
                    foreach (ParameterDefinition param in invoke2.Parameters)
                        invokeBegin2.Parameters.Add(new ParameterDefinition(param.Name, param.Attributes, param.ParameterType));
                    invokeBegin2.Parameters.Add(new ParameterDefinition("callback", ParameterAttributes.None, t_AsyncCallback));
                    invokeBegin2.Parameters.Add(new ParameterDefinition(null, ParameterAttributes.None, OutputModule.TypeSystem.Object));
                    invokeBegin2.Body = new MethodBody(invokeBegin2);
                    del.Methods.Add(invokeBegin2);
                }

            }

            return needVisibilityBackCompat;
        }

        private TypeReference GetImportedTypeAndCheckIfNoVisibleBackCompatSuffixNeeded(TypeReference typeRef,
            bool allowPrivateTypes, ref bool needBackCompactVisiblity) {

            TypeReference res;
            if (allowPrivateTypes) {
                res = ImportSafe(typeRef);
            } else {
                ImportVisibleResult importVisibleTypeRes = ImportVisible(typeRef);

                // We need the suffix when
                // 1. the type is not visible
                // 2. allow non visible types
                // 3. Need to keep back compatibility with older generated hookgen modules
                if (importVisibleTypeRes.Modified && NoVisibleCheck && NoVisibleCheckBackCompatSuffix) {
                    needBackCompactVisiblity = true;
                }
                res = importVisibleTypeRes.TypeRef;
            }

            return res;
        }

        string GetFriendlyName(MethodReference method) {
            string name = method.Name;
            if (name.StartsWith(".", StringComparison.Ordinal))
                name = name.Substring(1);
            name = name.Replace('.', '_');
            return name;
        }

        string GetFriendlyName(TypeReference type, bool full) {
            if (type is TypeSpecification) {
                StringBuilder builder = new StringBuilder();
                BuildFriendlyName(builder, type, full);
                return builder.ToString();
            }

            return full ? type.FullName : type.Name;
        }
        void BuildFriendlyName(StringBuilder builder, TypeReference type, bool full) {
            if (!(type is TypeSpecification)) {
                builder.Append((full ? type.FullName : type.Name).Replace("_", "", StringComparison.Ordinal));
                return;
            }

            if (type.IsByReference) {
                builder.Append("ref");
            } else if (type.IsPointer) {
                builder.Append("ptr");
            }

            BuildFriendlyName(builder, ((TypeSpecification) type).ElementType, full);

            if (type.IsArray) {
                builder.Append("Array");
            }
        }

        bool IsPublic(TypeDefinition typeDef) {
            return typeDef != null && (typeDef.IsNestedPublic || typeDef.IsPublic) && !typeDef.IsNotPublic;
        }

        bool HasPublicArgs(GenericInstanceType typeGen) {
            foreach (TypeReference arg in typeGen.GenericArguments) {
                // Generic parameter references are local.
                if (arg.IsGenericParameter)
                    return false;

                if (arg is GenericInstanceType argGen && !HasPublicArgs(argGen))
                    return false;

                if (!IsPublic(arg.SafeResolve()))
                    return false;
            }

            return true;
        }

        public struct ImportVisibleResult {
            public TypeReference TypeRef;
            public bool Modified;
        }

        // Returns Modified = true if the typeRef was modified due to being non visible.
        ImportVisibleResult ImportVisible(TypeReference typeRef) {
            bool modified = false;

            // Check if the declaring type is accessible.
            // If not, use its base type instead.
            // Note: This will break down with type specifications!
            TypeDefinition type = typeRef?.SafeResolve();
            goto Try;

            Retry:
            typeRef = type.BaseType;
            modified = true;
            type = typeRef?.SafeResolve();

            Try:
            if (type == null) { // Unresolvable - probably private anyway.
                modified = true;
                return new ImportVisibleResult {
                    TypeRef = OutputModule.TypeSystem.Object,
                    Modified = modified
                };
            }

            // Generic instance types are special. Try to match them exactly or baseify them.
            if (typeRef is GenericInstanceType typeGen && !HasPublicArgs(typeGen))
                goto Retry;

            // Check if the type and all of its parents are public.
            // Generic return / param types are too complicated at the moment and will be simplified.
            for (TypeDefinition parent = type; parent != null; parent = parent.DeclaringType) {
                if (IsPublic(parent) && (parent == type || !parent.HasGenericParameters))
                    continue;
                // If it isn't public, ...

                if (type.IsEnum) {
                    // ... try the enum's underlying type.
                    typeRef = type.FindField("value__").FieldType;
                    modified = true;
                    break;
                }

                // ... try the base type.
                goto Retry;
            }

            return new ImportVisibleResult {
                TypeRef = ImportSafe(typeRef),
                Modified = modified
            };
        }

        TypeReference ImportSafe(TypeReference typeRef) {
            try {
                return OutputModule.ImportReference(typeRef);
            } catch {
                // Under rare circumstances, ImportReference can fail, f.e. Private<K> : Public<K, V>
                return OutputModule.TypeSystem.Object;
            }
        }

        CustomAttribute GenerateObsolete(string message, bool error) {
            CustomAttribute attrib = new CustomAttribute(m_ObsoleteAttribute_ctor);
            attrib.ConstructorArguments.Add(new CustomAttributeArgument(OutputModule.TypeSystem.String, message));
            attrib.ConstructorArguments.Add(new CustomAttributeArgument(OutputModule.TypeSystem.Boolean, error));
            return attrib;
        }

        CustomAttribute GenerateEditorBrowsable(EditorBrowsableState state) {
            CustomAttribute attrib = new CustomAttribute(m_EditorBrowsableAttribute_ctor);
            attrib.ConstructorArguments.Add(new CustomAttributeArgument(t_EditorBrowsableState, state));
            return attrib;
        }

    }
}
