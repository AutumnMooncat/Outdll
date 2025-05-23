using Deadpan.Enums.Engine.Components.Modding;
using HarmonyLib;
using HarmonyLib.Public.Patching;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Outdll
{
    public class Outdll : WildfrostMod
    {
        public Outdll(string modDirectory) : base(modDirectory)
        {
            instance = this;
        }

        internal static Outdll instance;
        public override string GUID => "autumnmooncat.wildfrost.outdll";
        public override string[] Depends => new string[] { };
        public override string Title => "Outdll";
        public override string Description => "Outputs patch data for debugging purposes.";
        internal string OutputDirectory => Path.Combine(ModDirectory, "output");
        internal static List<string> logs = new List<string>();

        internal static void Print(string str)
        {
            System.Console.WriteLine("[Outdll] " + str);
            logs.Add(str);
        }

        public override void Load()
        {
            base.Load();
            Print($"Clearing output directory");
            Directory.Delete(OutputDirectory, true);
            Directory.CreateDirectory(OutputDirectory);
            Print("Performing Dump");
            PerformDump();
            Print("Dump Performed, unloading");
            using (StreamWriter outputFile = new StreamWriter(Path.Combine(OutputDirectory, "Log.txt")))
            {
                foreach (string log in logs)
                {
                    outputFile.WriteLine(log);
                }
            }
            logs.Clear();
            base.Unload();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo()
            {
                FileName = OutputDirectory+Path.DirectorySeparatorChar,
                UseShellExecute = true,
                Verb = "open"
            });
        }

        public override void Unload()
        {
            base.Unload();
        }

        private static void PerformDump()
        {
            Dictionary<Assembly, List<MethodBase>> assemblyDict = new Dictionary<Assembly, List<MethodBase>>();
            /*foreach (MethodBase info in Harmony.GetAllPatchedMethods()) {
                Print($"Found {info.Name} of {info.DeclaringType?.Name} in {info.DeclaringType?.Assembly?.GetName()?.Name}");
                Assembly owner = info.DeclaringType.Assembly;
                List<MethodBase> methods = patchRef.GetValueOrDefault(owner, new List<MethodBase>());
                methods.Add(info);
            }*/
            foreach (MethodBase method in Harmony.GetAllPatchedMethods())
            {
                Print($"Found Patch for {method.Name} of {method.DeclaringType?.Name} in {method.DeclaringType?.Assembly?.GetName()?.Name}");
                Assembly assembly = method.DeclaringType.Assembly;
                if (!assemblyDict.ContainsKey(assembly))
                {
                    assemblyDict.Add(assembly, new List<MethodBase>());
                }
                List<MethodBase> reference = assemblyDict[assembly];
                reference.Add(method);
            }

            /*foreach (var pair in (List<KeyValuePair<WeakReference, MethodBase>>)AccessTools.Field(typeof(PatchManager), "ReplacementToOriginals").GetValue(null))
            {
                MethodBase newMethod = pair.Key.IsAlive ? pair.Key.Target is MethodBase mbase ? mbase : null : null;
                MethodBase oldMethod = pair.Value;
                if (newMethod == null)
                {
                    Print($"We lost the reference for {newMethod}");
                    continue;
                }
                Print($"Found Patch for {oldMethod.Name} of {oldMethod.DeclaringType?.Name} in {oldMethod.DeclaringType?.Assembly?.GetName()?.Name}");
                Assembly assembly = oldMethod.DeclaringType.Assembly;
                if (!assemblyDict.ContainsKey(assembly))
                {
                    assemblyDict.Add(assembly, new List<PatchedMethodReference>());
                }
                List<PatchedMethodReference> reference = assemblyDict[assembly];
                reference.Add(new PatchedMethodReference()
                {
                    patchedMethod = newMethod,
                    originalMethod = oldMethod
                });
            }*/

            foreach (var pair in assemblyDict)
            {
                Assembly assembly = pair.Key;
                Print($"Dumping {assembly.GetName().Name}");
                ModuleDefinition module = ModuleDefinition.CreateModule(assembly.GetName().Name + "_patched", ModuleKind.Dll);
                foreach (MethodBase method in pair.Value)
                {
                    Print($"Dumping {assembly.GetName().Name} - {method.Name}");
                    TypeInfo typeInfo = method.DeclaringType.GetTypeInfo();
                    List<TypeInfo> superStack = new List<TypeInfo>();
                    TypeDefinition tdef = null;
                    
                    // Build nested types if needed
                    while (typeInfo != null)
                    {
                        superStack.Insert(0, typeInfo);
                        typeInfo = typeInfo.DeclaringType?.GetTypeInfo();
                    }
                    var types = module.Types;
                    foreach (var item in superStack)
                    {
                        TypeDefinition match = types.Where(def => def.Name == item.Name).FirstOrDefault();
                        if (match != null)
                        {
                            tdef = match;
                        }
                        else
                        {
                            TypeDefinition curr = new TypeDefinition(item.Namespace, item.Name, (Mono.Cecil.TypeAttributes)(uint)item.Attributes);
                            if (tdef == null)
                            {
                                module.Types.Add(curr);
                            }
                            else
                            {
                                tdef.NestedTypes.Add(curr);
                            }
                            tdef = curr;
                        }
                        types = tdef.NestedTypes;
                    }

                    // Create and repatch as we cannot access MethodBody of a patched method
                    PatchInfo patchInfo = method.GetPatchInfo();
                    Print($"PatchInfo has {patchInfo.prefixes.Length} prefixes, {patchInfo.postfixes.Length} postfixes, {patchInfo.transpilers.Length} transpilers, {patchInfo.finalizers.Length} finalizers");
                    MethodPatcher methodPatcher = method.GetMethodPatcher();
                    DynamicMethodDefinition dynamicMethodDefinition = methodPatcher.CopyOriginal();
                    if (dynamicMethodDefinition == null)
                    {
                        Print($"Failed to create DynamicMethodDefinition");
                        continue;
                    }
                    ILContext ctx = new ILContext(dynamicMethodDefinition.Definition);
                    HarmonyManipulator.Manipulate(method, patchInfo, ctx);
                    MethodDefinition mdef = dynamicMethodDefinition.Definition;

                    // Prep for export
                    mdef.DeclaringType = null;
                    tdef.Methods.Add(mdef);
                    ImportReferences(module, mdef.Body.Instructions);
                    Print($"Importing Return {mdef.ReturnType}");
                    mdef.ReturnType = module.ImportReference(mdef.ReturnType);
                    foreach (var item in mdef.Parameters)
                    {
                        Print($"Importing Param {item.ParameterType}");
                        item.ParameterType = module.ImportReference(item.ParameterType);
                    }
                    Mono.Collections.Generic.Collection<GenericParameter> gparams = new Mono.Collections.Generic.Collection<GenericParameter>();
                    foreach (var item in mdef.GenericParameters)
                    {
                        Print($"Importing GenericParam {item}");
                        gparams.Add((GenericParameter)module.ImportReference(item));
                    }
                    mdef.GenericParameters.Clear();
                    mdef.GenericParameters.AddRange(gparams);
                    foreach (var item in mdef.Body.Variables)
                    {
                        Print($"Importing Local {item.VariableType}");
                        item.VariableType = module.ImportReference(item.VariableType);
                    }
                    foreach (var item in mdef.Body.ExceptionHandlers)
                    {
                        if (item.CatchType == null)
                        {
                            continue;
                        }
                        Print($"Importing CatchType {item.CatchType}");
                        item.CatchType = module.ImportReference(item.CatchType);
                    }
                }
                module.Write(Path.Combine(instance.OutputDirectory, module.Name+".dll"));
                Print($"Wrote {module.Name} to output folder");
            }
        }

        internal static void ImportReferences(ModuleDefinition module, Mono.Collections.Generic.Collection<Instruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                object op = instruction.Operand;
                if (op != null)
                {
                    if (op is Type t)
                    {
                        Print($"Importing Type {t}");
                        instruction.Operand = module.ImportReference(t);
                    }
                    else if (op is FieldInfo fi)
                    {
                        Print($"Importing FieldInfo {fi}");
                        instruction.Operand = module.ImportReference(fi);
                    }
                    else if (op is MethodBase mb)
                    {
                        Print($"Importing MethodBase {mb}");
                        instruction.Operand = module.ImportReference(mb);
                    }
                    else if (op is TypeReference tr)
                    {
                        Print($"Importing TypeReference {tr}");
                        instruction.Operand = module.ImportReference(tr);
                    }
                    else if (op is FieldReference fr)
                    {
                        Print($"Importing FieldReference {fr}");
                        instruction.Operand = module.ImportReference(fr);
                    }
                    else if (op is MethodReference mr)
                    {
                        Print($"Importing MethodReference {mr}");
                        instruction.Operand = module.ImportReference(mr);
                        foreach (var item in mr.Parameters)
                        {
                            if (item.ParameterType.IsGenericParameter)
                            {
                                continue;
                            }

                            try
                            {
                                Print($"Importing MethodReference's' Param {item.ParameterType}");
                                item.ParameterType = module.ImportReference(item.ParameterType);
                            }
                            catch (Exception)
                            {
                                Print($"Importing Exploded");
                            }
                        }
                        try
                        {
                            Print($"Importing MethodReference's' Type {mr.DeclaringType}");
                            mr.DeclaringType = module.ImportReference(mr.DeclaringType);
                        }
                        catch (Exception)
                        {
                            Print($"Importing Exploded");
                        }
                    }
                }
            }
        }
    }

    internal class PatchedMethodReference
    {
        public MethodBase patchedMethod;

        public MethodBase originalMethod;
    }

    [HarmonyPatch]
    internal static class OutdllPatches
    {
        [HarmonyPatch]
        static class AddMethodLog
        {
            public static MethodBase TargetMethod()
            {
                return AccessTools.Method(AccessTools.TypeByName("MetadataBuilder"),"AddMethod");
            }

            public static void Prefix(object __instance, MethodDefinition method)
            {
                Outdll.Print($"Writing method {method.Name}");
            }
        }
    }
}
