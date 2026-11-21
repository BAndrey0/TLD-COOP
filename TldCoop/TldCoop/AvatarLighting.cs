using System.Collections.Generic;
using Il2Cpp;
using UnityEngine;
using UnityEngine.Rendering;

namespace TldCoop;

internal static class AvatarLighting
{
    private static readonly int Emission = Shader.PropertyToID("_EmissionColor");
    private static readonly int AmbientRed = Shader.PropertyToID("unity_SHAr");
    private static readonly int AmbientGreen = Shader.PropertyToID("unity_SHAg");
    private static readonly int AmbientBlue = Shader.PropertyToID("unity_SHAb");
    private static readonly int[] DirectionalAmbient =
    {
        Shader.PropertyToID("unity_SHBr"), Shader.PropertyToID("unity_SHBg"),
        Shader.PropertyToID("unity_SHBb"), Shader.PropertyToID("unity_SHC")
    };
    private static readonly List<Avatar> Avatars = new();
    private static MaterialPropertyBlock block;
    private static float nextUpdate;

    private sealed class Avatar
    {
        internal GameObject Root;
        internal List<(Renderer Renderer, ReflectionProbeUsage Reflections)> Renderers = new();
        internal Color? Ambient;
        internal bool Indoors;
    }

    internal static void Apply(GameObject avatar)
    {
        var entry = new Avatar { Root = avatar };
        foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true))
            if (renderer.TryCast<SkinnedMeshRenderer>() != null || renderer.TryCast<MeshRenderer>() != null)
                entry.Renderers.Add((renderer, renderer.reflectionProbeUsage));
        Avatars.Add(entry);
    }

    internal static void Tick()
    {
        if (Avatars.Count == 0 || Time.realtimeSinceStartup < nextUpdate) return;
        nextUpdate = Time.realtimeSinceStartup + .25f;
        bool inWorld = GearCatalogIndex.InWorld;
        bool indoors = inWorld && GameManager.GetWeatherComponent()?.IsIndoorEnvironment() == true;
        Color? ambient = !inWorld ? null : indoors ? Color.black : GameManager.GetUniStorm() != null
            ? Shader.GetGlobalColor(UniStormWeatherSystem.s_AmbientLightColorShaderID) : null;
        for (int i = Avatars.Count - 1; i >= 0; i--)
        {
            var entry = Avatars[i];
            if (entry.Root == null) { Avatars.RemoveAt(i); continue; }
            if (!ambient.HasValue || !entry.Root.activeInHierarchy || (entry.Indoors == indoors && entry.Ambient is { } before &&
                ((Vector4)(ambient.Value - before)).sqrMagnitude < .0004f)) continue;
            entry.Ambient = ambient;
            entry.Indoors = indoors;
            block ??= new MaterialPropertyBlock();
            foreach (var (renderer, reflections) in entry.Renderers)
            {
                if (renderer == null) continue;
                renderer.receiveShadows = true;
                renderer.lightProbeUsage = LightProbeUsage.CustomProvided;
                renderer.reflectionProbeUsage = indoors ? ReflectionProbeUsage.Off : reflections;
                renderer.GetPropertyBlock(block);
                // The sky probe is inappropriate inside enclosed rooms. Scene lights still illuminate the avatar.
                block.SetVector(AmbientRed, new Vector4(0, 0, 0, ambient.Value.r));
                block.SetVector(AmbientGreen, new Vector4(0, 0, 0, ambient.Value.g));
                block.SetVector(AmbientBlue, new Vector4(0, 0, 0, ambient.Value.b));
                foreach (int property in DirectionalAmbient) block.SetVector(property, Vector4.zero);
                if (renderer.TryCast<SkinnedMeshRenderer>() != null) block.SetColor(Emission, Color.black);
                renderer.SetPropertyBlock(block);
            }
        }
    }
}
