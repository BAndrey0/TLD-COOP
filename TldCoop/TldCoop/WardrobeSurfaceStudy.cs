using System;
using UnityEngine;
using Point = System.Numerics.Vector3;

namespace TldCoop;

// QA reference implementation. Evaluate the surface in the same fitting pose
// as the old experiment so its deformation can be compared on identical topology.
internal sealed class WardrobeSurfaceStudy
{
    private readonly record struct Face(int A, int B, int C, Point Origin, Point U, Point V, float UU, float UV, float VV, float Inverse, bool Arm);
    private readonly Face[] faces;
    private readonly BoneWeight[] weights;

    internal WardrobeSurfaceStudy(Vector3[] source, int[] indices, bool[] arms, BoneWeight[] weights)
    {
        this.weights = weights;
        var points = Array.ConvertAll(source, p => new Point(p.x, p.y, p.z));
        var usable = new System.Collections.Generic.List<Face>(indices.Length / 3);
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            int a = indices[i], b = indices[i + 1], c = indices[i + 2];
            var u = points[b] - points[a];
            var v = points[c] - points[a];
            float uu = Point.Dot(u, u), uv = Point.Dot(u, v), vv = Point.Dot(v, v);
            float denominator = uu * vv - uv * uv;
            if (denominator <= 1e-14f) continue;
            bool arm = (arms[a] ? 1 : 0) + (arms[b] ? 1 : 0) + (arms[c] ? 1 : 0) >= 2;
            usable.Add(new Face(a, b, c, points[a], u, v, uu, uv, vv, 1f / denominator, arm));
        }
        if (usable.Count == 0) throw new InvalidOperationException("A reference coat needs nondegenerate triangles.");
        faces = usable.ToArray();
    }

    internal BoneWeight Sample(Vector3 position, bool sleeve, float height)
    {
        var point = new Point(position.x, position.y, position.z);
        Face nearest = default;
        Point blending = default;
        float best = float.PositiveInfinity;
        foreach (ref readonly var face in faces.AsSpan())
        {
            var offset = point - face.Origin;
            var barycentric = Project(face, offset);
            float score = (offset - face.U * barycentric.Y - face.V * barycentric.Z).LengthSquared();
            if (height < .82f && face.Arm != sleeve) score += .045f;
            if (score >= best) continue;
            best = score;
            nearest = face;
            blending = barycentric;
        }

        Span<int> bones = stackalloc int[12];
        Span<float> influences = stackalloc float[12];
        int count = 0;
        Add(weights[nearest.A], blending.X, bones, influences, ref count);
        Add(weights[nearest.B], blending.Y, bones, influences, ref count);
        Add(weights[nearest.C], blending.Z, bones, influences, ref count);
        Span<int> indices = stackalloc int[4];
        Span<float> values = stackalloc float[4];
        indices.Clear();
        values.Clear();
        for (int rank = 0; rank < 4; rank++)
        {
            int largest = -1;
            for (int i = 0; i < count; i++)
                if (influences[i] > 0f && (largest < 0 || influences[i] > influences[largest])) largest = i;
            if (largest < 0) break;
            indices[rank] = bones[largest];
            values[rank] = influences[largest];
            influences[largest] = 0f;
        }
        float sum = values[0] + values[1] + values[2] + values[3];
        if (sum < 1e-6f) return weights[nearest.A];
        return new BoneWeight
        {
            boneIndex0 = indices[0], boneIndex1 = indices[1], boneIndex2 = indices[2], boneIndex3 = indices[3],
            weight0 = values[0] / sum, weight1 = values[1] / sum, weight2 = values[2] / sum, weight3 = values[3] / sum
        };
    }

    private static Point Project(in Face face, Point point)
    {
        float pu = Point.Dot(point, face.U), pv = Point.Dot(point, face.V);
        float y = (pu * face.VV - pv * face.UV) * face.Inverse;
        float z = (pv * face.UU - pu * face.UV) * face.Inverse;
        if (y >= 0f && z >= 0f && y + z <= 1f) return new Point(1f - y - z, y, z);

        float u = Math.Clamp(pu / face.UU, 0f, 1f);
        float v = Math.Clamp(pv / face.VV, 0f, 1f);
        var edge = face.V - face.U;
        var fromU = point - face.U;
        float t = Math.Clamp(Point.Dot(fromU, edge) / edge.LengthSquared(), 0f, 1f);
        float distanceU = (point - face.U * u).LengthSquared();
        float distanceV = (point - face.V * v).LengthSquared();
        float distanceEdge = (fromU - edge * t).LengthSquared();
        if (distanceU <= distanceV && distanceU <= distanceEdge) return new Point(1f - u, u, 0f);
        return distanceV <= distanceEdge ? new Point(1f - v, 0f, v) : new Point(0f, 1f - t, t);
    }

    private static void Add(BoneWeight weight, float factor, Span<int> bones, Span<float> influences, ref int count)
    {
        AddInfluence(weight.boneIndex0, weight.weight0 * factor, bones, influences, ref count);
        AddInfluence(weight.boneIndex1, weight.weight1 * factor, bones, influences, ref count);
        AddInfluence(weight.boneIndex2, weight.weight2 * factor, bones, influences, ref count);
        AddInfluence(weight.boneIndex3, weight.weight3 * factor, bones, influences, ref count);
    }

    private static void AddInfluence(int bone, float weight, Span<int> bones, Span<float> influences, ref int count)
    {
        if (weight <= 0f) return;
        int index = bones[..count].IndexOf(bone);
        if (index >= 0) influences[index] += weight;
        else
        {
            bones[count] = bone;
            influences[count++] = weight;
        }
    }
}
