using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using GameServer;
using UnityEngine;
using UnityEngine.Profiling;
using Object = UnityEngine.Object;

namespace TldCoop;

internal static class WardrobeStudy
{
    private const string Gear = "GEAR_DownParka";
    private sealed record Fixture(GameObject Avatar, Animator Animator, Comps.MultiplayerPlayerClothingManager Wardrobe, SkinnedMeshRenderer Coat);

    internal static void Run(GameObject prefab, string output, Action<string, Vector3, float> capture)
    {
        using var report = new StreamWriter(output);
        string prefix = Path.ChangeExtension(output, null);
        CaptureItemForms(prefix, capture);
        var outfit = new DataStr.PlayerClothingData
        {
            m_Top = Gear, m_Hat = "GEAR_BasicWoolHat", m_Scarf = "GEAR_BasicWoolScarf",
            m_Gloves = "GEAR_SkiGloves", m_Boots = "GEAR_InsulatedBoots", m_Bottom = "GEAR_InsulatedPants"
        };
        using var outgoing = new Packet();
        outgoing.Write(outfit);
        using var incoming = new Packet(outgoing.ToArray());
        var decoded = incoming.ReadClothingData();
        if (decoded.m_Top != Gear || decoded.m_Gloves != outfit.m_Gloves || decoded.m_Appearance != -1)
            throw new InvalidOperationException("Standard wardrobe packet round trip failed.");
        report.WriteLine("PASS standard wardrobe packet codec (local)");

        var female = Spawn(prefab, 1, 0f, decoded);
        var fixtures = new[] { Spawn(prefab, 0, 1.25f, decoded), Spawn(prefab, 0, 0f, decoded), Spawn(prefab, 0, -1.25f, decoded) };
        var labels = new[] { "stock", "previous-nearest", "surface-interpolated" };
        Describe(report, "standard female", female);
        Describe(report, "standard male", fixtures[0]);
        report.WriteLine($"same coat mesh and number of bones across sexes={female.Coat.sharedMesh == fixtures[0].Coat.sharedMesh && female.Coat.bones.Length == fixtures[0].Coat.bones.Length}");
        report.WriteLine($"same AnimatorController across sexes={female.Animator.runtimeAnimatorController == fixtures[0].Animator.runtimeAnimatorController}");
        report.WriteLine("controller clips " + string.Join(", ", fixtures[0].Animator.runtimeAnimatorController.animationClips.Select(clip => clip.name).Distinct()));
        female.Avatar.SetActive(false);
        Object.Destroy(female.Avatar);

        var stock = fixtures[0].Coat.sharedMesh;
        for (int i = 1; i < fixtures.Length; i++)
        {
            var coat = fixtures[i].Coat;
            var timer = Stopwatch.StartNew();
            var trial = AvatarWardrobe.StudyCoat(fixtures[i].Avatar.transform, coat, Gear, surface: i == 2);
            timer.Stop();
            if (trial.Mesh == null || trial.Materials == null) throw new InvalidOperationException(labels[i] + " coat unavailable.");
            coat.sharedMesh = trial.Mesh;
            coat.sharedMaterials = trial.Materials;
            report.WriteLine($"{labels[i]} buildMs={timer.Elapsed.TotalMilliseconds:F2}");
            Describe(report, labels[i], fixtures[i]);
        }

        var bake = new Mesh();
        try
        {
            var idle = new Vector3[fixtures.Length][];
            var poses = new (string Label, string State, float Time, float Yaw, float Center)[]
            {
                ("idle", "Idle", .24f, 0f, .95f),
                ("side", "Idle", .24f, 58f, .95f),
                ("rear", "Idle", .24f, 158f, .95f),
                ("walk", "Walk", .25f, 24f, .95f),
                ("run", "Run", .42f, -25f, .95f),
                ("crouch", "Ctrl", .35f, 22f, .76f)
            };
            foreach (var pose in poses)
            {
                for (int i = 0; i < fixtures.Length; i++)
                {
                    var fixture = fixtures[i];
                    fixture.Avatar.transform.rotation = Quaternion.Euler(0, pose.Yaw, 0);
                    int state = Animator.StringToHash(pose.State);
                    if (!fixture.Animator.HasState(0, state)) throw new InvalidOperationException("Missing animation state " + pose.State);
                    fixture.Animator.Play(state, 0, pose.Time);
                    fixture.Animator.Update(pose.State == "Ctrl" ? .35f : 0f);
                    var clips = fixture.Animator.GetCurrentAnimatorClipInfo(0);
                    if (clips.Length == 0) throw new InvalidOperationException("No active clip for " + pose.State);
                    fixture.Coat.BakeMesh(bake);
                    Vector3[] vertices = bake.vertices;
                    if (idle[i] == null) idle[i] = vertices;
                    report.WriteLine($"{pose.Label} {labels[i]} clip={clips[0].clip.name} edgeStretch95={Stretch95(fixture.Coat.sharedMesh, idle[i], vertices):F3} bounds={bake.bounds.size:F3}");
                }
                capture(prefix + "-" + pose.Label + ".jpg", new Vector3(0, 1.05f, 4.25f), pose.Center);
            }

            foreach (int i in new[] { 0, 1, 2 })
            {
                var coat = fixtures[i].Coat;
                const int count = 48;
                var sample = new double[count];
                coat.BakeMesh(bake);
                for (int frame = 0; frame < count; frame++)
                {
                    long start = Stopwatch.GetTimestamp();
                    coat.BakeMesh(bake);
                    sample[frame] = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                }
                Array.Sort(sample);
                report.WriteLine($"{labels[i]} isolatedCpuBakeMedianMs={sample[count / 2]:F3}");
            }
        }
        finally { Object.Destroy(bake); }

        var switched = fixtures[2];
        switched.Coat.sharedMesh = stock;
        switched.Coat.sharedMaterials = fixtures[0].Coat.sharedMaterials;
        switched.Wardrobe.ApplyLocalAppearance(switched.Avatar, -1, 0, new DataStr.PlayerClothingData());
        if (switched.Coat.gameObject.activeInHierarchy) throw new InvalidOperationException("Removed outfit still displays the coat.");
        switched.Wardrobe.ApplyLocalAppearance(switched.Avatar, -1, 0, decoded);
        if (!switched.Coat.gameObject.activeInHierarchy || switched.Coat.sharedMesh != stock)
            throw new InvalidOperationException("Stock coat was not restored after re-equipping.");
        report.WriteLine("PASS prototype detached, standard coat disappeared and returned after costume change");
        foreach (var fixture in fixtures) Object.Destroy(fixture.Avatar);
        report.WriteLine("PASS completed standard avatar wardrobe study");
    }

    private static void CaptureItemForms(string prefix, Action<string, Vector3, float> capture)
    {
        var item = MyMod.GetGearItemPrefab(Gear);
        if (item == null) throw new InvalidOperationException("Missing asset " + Gear);
        var forms = new[] { "Mesh", "MeshInspectMode" };
        var copies = new GameObject[forms.Length];
        try
        {
            for (int i = 0; i < forms.Length; i++)
            {
                var source = item.GetComponentsInChildren<MeshFilter>(true).First(f => f.name == forms[i]);
                var copy = copies[i] = new GameObject("Study " + forms[i]);
                copy.layer = 30;
                copy.transform.SetPositionAndRotation(new Vector3(i == 0 ? .62f : -.62f, .70f, 0f), source.transform.localRotation);
                copy.AddComponent<MeshFilter>().sharedMesh = source.sharedMesh;
                copy.AddComponent<MeshRenderer>().sharedMaterials = source.GetComponent<MeshRenderer>().sharedMaterials;
            }
            capture(prefix + "-item-forms.jpg", new Vector3(0f, 1.6f, 2.7f), .70f);
        }
        finally
        {
            foreach (var copy in copies)
                if (copy != null)
                {
                    copy.SetActive(false);
                    Object.Destroy(copy);
                }
        }
    }

    private static Fixture Spawn(GameObject prefab, int sex, float x, DataStr.PlayerClothingData outfit)
    {
        var avatar = Object.Instantiate(prefab, new Vector3(x, 0f, 0f), Quaternion.identity);
        foreach (var collider in avatar.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
        foreach (var transform in avatar.GetComponentsInChildren<Transform>(true)) transform.gameObject.layer = 30;
        var animator = avatar.GetComponent<Animator>();
        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        var wardrobe = avatar.AddComponent<Comps.MultiplayerPlayerClothingManager>();
        wardrobe.enabled = false;
        wardrobe.ApplyLocalAppearance(avatar, -1, sex, outfit);
        var coat = avatar.transform.GetChild(0).GetChild(1).GetComponentInChildren<SkinnedMeshRenderer>(false);
        if (coat == null || NativeAppearance.HasCostume(avatar)) throw new InvalidOperationException("Expected the standard avatar and coat.");
        return new Fixture(avatar, animator, wardrobe, coat);
    }

    private static void Describe(StreamWriter report, string label, Fixture fixture)
    {
        var skin = fixture.Coat;
        var mesh = skin.sharedMesh;
        int triangles = mesh.triangles.Length / 3;
        var active = fixture.Avatar.GetComponentsInChildren<Renderer>(false).Where(r => r.enabled && !r.forceRenderingOff).ToArray();
        int skinned = active.Count(r => r.TryCast<SkinnedMeshRenderer>() != null);
        int slots = active.Sum(r => r.sharedMaterials.Length);
        report.WriteLine($"{label} renderer={skin.name} mesh={mesh.name} vertices={mesh.vertexCount} triangles={triangles} " +
            $"bindings={skin.bones.Length} rootBone={skin.rootBone?.name} bytes={Profiler.GetRuntimeMemorySizeLong(mesh)} " +
            $"uv0={mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord0)} " +
            $"coatMaterials={string.Join(",", skin.sharedMaterials.Select(mat => mat?.name))} activeRenderers={active.Length} skinned={skinned} materialSlots={slots}");
        foreach (var group in new[] { fixture.Avatar.transform.GetChild(0), fixture.Avatar.transform.GetChild(1) })
        {
            foreach (var child in group.GetComponentsInChildren<SkinnedMeshRenderer>(false))
            {
                if (!child.enabled || child.sharedMesh == null || child == skin) continue;
                report.WriteLine($"  {child.name}: {child.sharedMesh.name} vertices={child.sharedMesh.vertexCount} bones={child.bones.Length}");
            }
        }
    }

    private static float Stretch95(Mesh mesh, Vector3[] idle, Vector3[] moved)
    {
        var triangles = mesh.triangles;
        var ratios = new System.Collections.Generic.List<float>(triangles.Length);
        for (int i = 0; i < triangles.Length; i += 3)
        {
            for (int edge = 0; edge < 3; edge++)
            {
                int a = triangles[i + edge], b = triangles[i + (edge + 1) % 3];
                float rest = Vector3.Distance(idle[a], idle[b]);
                float pose = Vector3.Distance(moved[a], moved[b]);
                if (Mathf.Min(rest, pose) > .001f)
                    ratios.Add(Mathf.Max(pose / rest, rest / pose));
            }
        }
        ratios.Sort();
        return ratios.Count == 0 ? 0f : ratios[(int)(.95f * (ratios.Count - 1))];
    }
}
