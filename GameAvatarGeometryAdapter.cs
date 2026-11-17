using System.Numerics;
using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using SkyModel.Core.Models;
using WpfTransform3D = System.Windows.Media.Media3D.Transform3D;

namespace SkyCharacterStudio;

/// <summary>
/// Packs the fields consumed by Avatar-13h into Helix's default mesh streams.
/// The game vertex shader reads tangent/binormal only as packed varying inputs,
/// so those streams can carry the four half2 UV sets without changing its math.
/// </summary>
internal static class GameAvatarGeometryAdapter
{
    public static MeshGeometry3D Create(
        MeshData data,
        WpfTransform3D? itemTransform = null,
        System.Windows.Media.Media3D.Point3DCollection? posedPositions = null)
    {
        var count = data.Vertices.Count;
        var geometry = new MeshGeometry3D
        {
            Positions = new Vector3Collection(count),
            Normals = new Vector3Collection(count),
            Tangents = new Vector3Collection(count),
            BiTangents = new Vector3Collection(count),
            TextureCoordinates = new Vector2Collection(count),
            Colors = new Color4Collection(count),
            TriangleIndices = new IntCollection(data.Indices.Count * 3),
        };

        for (var i = 0; i < count; i++)
        {
            var (x, y, z) = data.Vertices[i];
            var position = posedPositions is { Count: > 0 } && posedPositions.Count == count
                ? posedPositions[i]
                : new System.Windows.Media.Media3D.Point3D(-x, y, -z);
            if (itemTransform is not null) position = itemTransform.Transform(position);
            geometry.Positions.Add(new Vector3((float)position.X, (float)position.Y, (float)position.Z));
        }

        foreach (var (a, b, c) in data.Indices)
        {
            if (a < 0 || b < 0 || c < 0 || a >= count || b >= count || c >= count) continue;
            geometry.TriangleIndices.Add(a);
            geometry.TriangleIndices.Add(b);
            geometry.TriangleIndices.Add(c);
        }

        var normals = new Vector3[count];
        for (var i = 0; i + 2 < geometry.TriangleIndices.Count; i += 3)
        {
            var a = geometry.TriangleIndices[i];
            var b = geometry.TriangleIndices[i + 1];
            var c = geometry.TriangleIndices[i + 2];
            var normal = Vector3.Cross(geometry.Positions[b] - geometry.Positions[a], geometry.Positions[c] - geometry.Positions[a]);
            normals[a] += normal;
            normals[b] += normal;
            normals[c] += normal;
        }

        for (var i = 0; i < count; i++)
        {
            var normal = normals[i];
            if (normal.LengthSquared() > 1e-12f) normal = Vector3.Normalize(normal);
            geometry.Normals.Add(normal);

            var uv0 = At(data.Uv0, i);
            var uv1 = At(data.Uv1, i);
            var uv2 = At(data.Uv2, i);
            var uv3 = At(data.Uv3, i);
            geometry.Tangents.Add(new Vector3(uv0.U, uv0.V, uv1.U));
            geometry.BiTangents.Add(new Vector3(uv1.V, uv2.U, uv2.V));
            geometry.TextureCoordinates.Add(new Vector2(uv3.U, uv3.V));

            // Avatar-13h consumes COLOR0. TgcMeshReader preserves the four
            // bytes that follow each position, which are the packed per-vertex
            // attribute stream. D3D's normalized color input expects 0..1.
            if (data.PackedVertexAttrs.Count > i)
            {
                var packed = data.PackedVertexAttrs[i];
                geometry.Colors.Add(new Color4(packed.B0 / 255f, packed.B1 / 255f, packed.B2 / 255f, packed.B3 / 255f));
            }
            else
            {
                geometry.Colors.Add(new Color4(1, 1, 1, 1));
            }
        }

        return geometry;
    }

    private static (float U, float V) At(IReadOnlyList<(float U, float V)> values, int index) =>
        values.Count > index ? values[index] : (0, 0);
}
