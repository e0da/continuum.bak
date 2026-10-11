using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    internal static class StartupPartConstructionTiming
    {
        sealed class Rule
        {
            public MethodInfo caller, target, wrapper;
            public int sites;
        }
        static readonly List<Rule> rules = new List<Rule>();
        static Action<PartLoader, GameObject> setupSkinned;
        static Action<PartLoader, GameObject, float> setFog;

        public static void Install(Harmony harmony)
        {
            if (rules.Count != 0) throw new InvalidOperationException("Part construction observer already installed.");
            MethodInfo parse = Pinned(typeof(PartLoader), "ParsePart", 100668679, typeof(UrlDir.UrlConfig), typeof(ConfigNode));
            MethodInfo config = Pinned(typeof(Part), "AddModule", 100671089, typeof(ConfigNode), typeof(bool));
            MethodInfo named = Pinned(typeof(Part), "AddModule", 100671088, typeof(string), typeof(bool));
            MethodInfo load = Pinned(typeof(PartModule), "Load", 100672895, typeof(ConfigNode));
            MethodInfo awake = Pinned(typeof(PartModule), "Awake", 100672893);
            MethodInfo skinned = Pinned(typeof(PartLoader), "SetupSkinnedMeshes", 100668681, typeof(GameObject));
            MethodInfo fog = Pinned(typeof(PartLoader), "SetFogFactor", 100668703, typeof(GameObject), typeof(float));
            setupSkinned = (Action<PartLoader, GameObject>)Delegate.CreateDelegate(typeof(Action<PartLoader, GameObject>), skinned);
            setFog = (Action<PartLoader, GameObject, float>)Delegate.CreateDelegate(typeof(Action<PartLoader, GameObject, float>), fog);
            Add(parse, skinned, nameof(Skinned));
            Add(parse, fog, nameof(Fog));
            Add(parse, Pinned(typeof(ConfigNode), "LoadObjectFromConfig", 100679457, typeof(object), typeof(ConfigNode), typeof(int), typeof(bool)), nameof(ConfigLoad));
            Add(parse, Pinned(typeof(PartLoader), "ApplyPartValue", 100668680, typeof(Part), typeof(ConfigNode.Value)), nameof(ApplyValue));
            Add(parse, config, nameof(AddConfig));
            Add(parse, named, nameof(AddNamed));
            Add(config, named, nameof(AddNamedFromConfig));
            Add(config, load, nameof(LoadModule));
            Add(config, awake, nameof(AwakeFromConfig));
            Add(named, typeof(GameObject).GetMethod("AddComponent", new[] { typeof(Type) }), nameof(Component));
            Add(named, awake, nameof(AwakeFromNamed));
            Add(load, Pinned(typeof(BaseFieldList), "Load", 100668142, typeof(ConfigNode)), nameof(LoadFields));
            Add(load, Pinned(typeof(PartModule), "OnLoad", 100672908, typeof(ConfigNode)), nameof(OnLoad));
            Add(awake, Pinned(typeof(PartModule), "OnAwake", 100672897), nameof(OnAwake));
            foreach (MethodInfo caller in rules.Select(x => x.caller).Distinct())
            {
                Patches patches = Harmony.GetPatchInfo(caller);
                if (patches != null && patches.Owners.Any(owner => owner != "continuum.startup-model-timing"))
                    throw new InvalidOperationException("Part construction caller already modified: " + caller);
                harmony.Patch(caller, transpiler: new HarmonyMethod(typeof(StartupPartConstructionTiming), nameof(Calls)));
            }
        }
        static MethodInfo Pinned(Type type, string name, int token, params Type[] arguments)
        {
            MethodInfo method = AccessTools.DeclaredMethod(type, name, arguments);
            if (method == null || method.MetadataToken != token || method.Module.ModuleVersionId != typeof(PartLoader).Module.ModuleVersionId)
                throw new InvalidOperationException("Pinned construction method differs: " + type.FullName + "." + name);
            return method;
        }
        static void Add(MethodInfo caller, MethodInfo target, string wrapperName)
        {
            MethodInfo wrapper = AccessTools.DeclaredMethod(typeof(StartupPartConstructionTiming), wrapperName);
            if (target == null || wrapper == null || wrapper.ReturnType != target.ReturnType)
                throw new InvalidOperationException("Construction call signature absent: " + wrapperName);
            Type[] expected = (target.IsStatic ? Enumerable.Empty<Type>() : new[] { target.DeclaringType }).Concat(target.GetParameters().Select(x => x.ParameterType)).ToArray();
            if (!wrapper.GetParameters().Select(x => x.ParameterType).SequenceEqual(expected))
                throw new InvalidOperationException("Construction wrapper stack shape differs: " + wrapperName);
            rules.Add(new Rule { caller = caller, target = target, wrapper = wrapper });
            StartupModelTiming.RegisterCallsite(wrapper, target, caller.DeclaringType.FullName + "." + caller.Name + " -> " + target);
        }
        static IEnumerable<CodeInstruction> Calls(IEnumerable<CodeInstruction> source, MethodBase __originalMethod)
        {
            var body = source.ToList();
            Rule[] selected = rules.Where(x => Equals(x.caller, __originalMethod)).ToArray();
            if (selected.Length == 0) throw new InvalidOperationException("Unexpected construction caller.");
            foreach (Rule rule in selected)
            {
                CodeInstruction[] sites = body.Where(instruction => (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && Equals(instruction.operand, rule.target)).ToArray();
                if (sites.Length != 1) throw new InvalidOperationException("Pinned construction call count differs: " + rule.wrapper.Name + " count=" + sites.Length);
                rule.sites = sites.Length;
            }
            foreach (Rule rule in selected)
                foreach (CodeInstruction instruction in body)
                    if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && Equals(instruction.operand, rule.target))
                    {
                        // Keep the original instruction's labels and exception-region markers at its call boundary.
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = rule.wrapper;
                    }
            return body;
        }
        public static bool Installed { get { return rules.Count == 14 && rules.All(x => x.sites == 1); } }
        public static bool HooksRemoved { get { return rules.Select(x => x.caller).Distinct().All(x => Harmony.GetPatchInfo(x)?.Owners.Contains("continuum.startup-model-timing") != true); } }
        static StartupModelTiming.Frame Begin(int index) { return StartupModelTiming.BeginCall(rules[index].wrapper); }

        static void Skinned(PartLoader loader, GameObject model)
        {
            StartupModelTiming.Frame frame = Begin(0); Exception error = null;
            try { setupSkinned(loader, model); }
            catch (Exception caught) { error = caught; throw; }
            finally { StartupModelTiming.FinishCall(frame, error); }
        }

        static void Fog(PartLoader loader, GameObject model, float factor)
        {
            StartupModelTiming.Frame frame = Begin(1); Exception error = null;
            try { setFog(loader, model, factor); }
            catch (Exception caught) { error = caught; throw; }
            finally { StartupModelTiming.FinishCall(frame, error); }
        }

        static bool ConfigLoad(object host, ConfigNode node, int pass, bool remove)
        {
            StartupModelTiming.Frame frame = Begin(2); Exception error = null;
            try { return ConfigNode.LoadObjectFromConfig(host, node, pass, remove); }
            catch (Exception caught) { error = caught; throw; }
            finally { StartupModelTiming.FinishCall(frame, error); }
        }

        static bool ApplyValue(Part part, ConfigNode.Value value)
        {
            StartupModelTiming.Frame frame = Begin(3); Exception error = null;
            try { return PartLoader.ApplyPartValue(part, value); }
            catch (Exception caught) { error = caught; throw; }
            finally { StartupModelTiming.FinishCall(frame, error); }
        }

        static PartModule AddConfig(Part part, ConfigNode node, bool forceAwake)
        {
            StartupModelTiming.Frame frame = Begin(4); Exception error = null;
            try { return part.AddModule(node, forceAwake); }
            catch (Exception caught) { error = caught; throw; }
            finally { StartupModelTiming.FinishCall(frame, error); }
        }

        static PartModule AddNamed(Part part, string name, bool forceAwake)
        {
            StartupModelTiming.Frame frame = Begin(5); Exception error = null;
            try { return part.AddModule(name, forceAwake); }
            catch (Exception caught) { error = caught; throw; }
            finally { StartupModelTiming.FinishCall(frame, error); }
        }

        static PartModule AddNamedFromConfig(Part part, string name, bool forceAwake)
        {
            StartupModelTiming.Frame frame = Begin(6); Exception error = null;
            try { return part.AddModule(name, forceAwake); }
            catch (Exception caught) { error = caught; throw; }
            finally { StartupModelTiming.FinishCall(frame, error); }
        }

        static void LoadModule(PartModule module, ConfigNode node)
        {
            StartupModelTiming.Frame frame = Begin(7); Exception error = null;
            try { module.Load(node); }
            catch (Exception caught) { error = caught; throw; }
            finally { StartupModelTiming.FinishCall(frame, error); }
        }

        static void AwakeFromConfig(PartModule module)
        {
            StartupModelTiming.Frame frame = Begin(8); Exception error = null;
            try { module.Awake(); }
            catch (Exception caught) { error = caught; throw; }
            finally { StartupModelTiming.FinishCall(frame, error); }
        }

        static Component Component(GameObject owner, Type type)
        {
            StartupModelTiming.Frame frame = Begin(9); Exception error = null;
            try { return owner.AddComponent(type); }
            catch (Exception caught) { error = caught; throw; }
            finally { StartupModelTiming.FinishCall(frame, error); }
        }

        static void AwakeFromNamed(PartModule module)
        {
            StartupModelTiming.Frame frame = Begin(10); Exception error = null;
            try { module.Awake(); }
            catch (Exception caught) { error = caught; throw; }
            finally { StartupModelTiming.FinishCall(frame, error); }
        }

        static void LoadFields(BaseFieldList fields, ConfigNode node)
        {
            StartupModelTiming.Frame frame = Begin(11); Exception error = null;
            try { fields.Load(node); }
            catch (Exception caught) { error = caught; throw; }
            finally { StartupModelTiming.FinishCall(frame, error); }
        }

        static void OnLoad(PartModule module, ConfigNode node)
        {
            StartupModelTiming.Frame frame = Begin(12); Exception error = null;
            try { module.OnLoad(node); }
            catch (Exception caught) { error = caught; throw; }
            finally { StartupModelTiming.FinishCall(frame, error); }
        }

        static void OnAwake(PartModule module)
        {
            StartupModelTiming.Frame frame = Begin(13); Exception error = null;
            try { module.OnAwake(); }
            catch (Exception caught) { error = caught; throw; }
            finally { StartupModelTiming.FinishCall(frame, error); }
        }
    }
}
