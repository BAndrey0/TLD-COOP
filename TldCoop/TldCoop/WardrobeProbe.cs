using System;
using System.IO;
using Il2CppTLD.AddressableAssets;
using UnityEngine;

namespace TldCoop;

internal static class WardrobeProbe
{
    internal static void Run(string output, bool includeStory = true)
    {
        using var report = new StreamWriter(output);
        void Dump(GameObject root)
        {
            if (root == null) { report.WriteLine("missing"); return; }
            report.WriteLine(root.name + " root=" + root.transform.localScale);
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var skin = renderer.TryCast<SkinnedMeshRenderer>();
                var mesh = skin?.sharedMesh ?? renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null) continue;
                var t = renderer.transform;
                int triangles = 0;
                for (int i = 0; i < mesh.subMeshCount; i++)
                    if (mesh.GetTopology(i) == MeshTopology.Triangles) triangles += (int)mesh.GetIndexCount(i) / 3;
                string path = t.name;
                for (var parent = t.parent; parent != null && parent != root.transform; parent = parent.parent)
                    path = parent.name + "/" + path;
                report.WriteLine($"{path}: {mesh.name} vertices={mesh.vertexCount} triangles={triangles} uv0={mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord0)} " +
                    $"submeshes={mesh.subMeshCount} renderer={(skin == null ? "MeshRenderer" : "SkinnedMeshRenderer")} bones={skin?.bones.Length ?? 0} " +
                    $"activeSelf={t.gameObject.activeSelf} size={mesh.bounds.size:F3} center={mesh.bounds.center:F3} localRot={t.localEulerAngles:F0} read={mesh.isReadable}");
                foreach (var m in renderer.sharedMaterials)
                {
                    if (m == null) continue;
                    report.Write($" mat {m.name} {m.shader?.name}");
                    foreach (string slot in new[] { "_MainTex", "_BumpMap", "_DamageTex" })
                        if (m.HasProperty(slot) && m.GetTexture(slot) is { } tex && tex != null)
                            report.Write($" {slot}={tex.name} ({tex.width}x{tex.height})");
                    report.WriteLine();
                }
            }
        }
        foreach (var name in new[] { "GEAR_DownParka", "GEAR_MilitaryParka", "GEAR_BasicWinterCoat", "GEAR_BearSkinCoat",
            "GEAR_Jeans", "GEAR_InsulatedPants", "GEAR_SkiGloves", "GEAR_BasicWoolHat", "GEAR_InsulatedBoots",
            "GEAR_BasicShoes", "GEAR_BasicWoolScarf" })
        {
            report.WriteLine(name);
            var gear = MyMod.GetGearItemPrefab(name);
            report.WriteLine(gear?.m_ClothingItem?.m_PaperDollTextureName);
            Dump(gear?.gameObject);
            foreach (var firstPerson in new[] { gear?.m_ClothingItem?.m_FirstPersonPrefabMale, gear?.m_ClothingItem?.m_FirstPersonPrefabFemale })
                if (firstPerson != null && firstPerson.RuntimeKeyIsValid())
                    Dump(AssetHelper.SafeLoadAssetAsync<GameObject>(firstPerson.RuntimeKey.ToString(), true).WaitForCompletion());
        }
        foreach (string asset in new[] { "CLTH_HMM_TOR_Down_Parka_INSP", "CLTH_HMM_TOR_Military_Parka_INSP",
            "CLTH_HMM_LEG_Jeans_INSP", "CLTH_HMF_LEG_Insulated_pants_INSP", "CLTH_HMM_SHO_Insulated_Boots_INSP",
            "CLTH_HMM_HAR_Wool_toque_INSP", "CLTH_HMM_HED_Wool_scarf_INSP" })
        {
            report.WriteLine(asset);
            Dump(AssetHelper.SafeLoadAssetAsync<GameObject>("Assets/ArtAssets/Characters/Clothes/" + asset + ".fbx", true).WaitForCompletion());
        }
        if (includeStory)
        {
            Dump(NativeCharacterLibrary.Load(0));
            Dump(NativeCharacterLibrary.Load(1));
        }
    }
}
