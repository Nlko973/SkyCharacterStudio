using System.Numerics;
using System.Text;
using System.IO;
using K4os.Compression.LZ4;

namespace SkyCharacterStudio;

internal sealed record SkyBone(string Name, int ParentIndex, Matrix4x4 InverseBind);
internal readonly record struct SkyPose(Vector3 Scale, Quaternion Rotation, Vector3 Translation);
internal sealed record SkyAnimationClip(string Name, IReadOnlyList<SkyBone> Bones, IReadOnlyList<SkyPose[]> Frames, int StartFrame, int EndFrame)
{
    public int FrameCount => Frames.Count;
}

/// <summary>Reads the game's local .animpack clips. Format layout and channel flags were checked against Sky.exe.</summary>
internal static class SkyAnimationReader
{
    public static IReadOnlyList<SkyAnimationClip> Read(string path, string? animationListPath = null)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 81) throw new InvalidDataException("The animpack header is incomplete.");
        var version = U32(data, 0);
        var name = ReadName(data, 4, 64);
        var boneCount = checked((int)U32(data, 0x44));
        var boneDefsFlag = U32(data, 0x48);
        var refSqtFlag = U32(data, 0x4C);
        var compression = data[0x50];
        if (boneCount is < 1 or > 512) throw new InvalidDataException($"Unexpected animpack bone count: {boneCount}.");
        if (compression > 2) throw new InvalidDataException($"Unsupported animpack compression mode: {compression}.");

        var bonesOffset = version >= 10 ? 0x55 : 0x51;
        Ensure(data, bonesOffset, checked(boneCount * 132));
        var bones = new List<SkyBone>(boneCount);
        for (var i = 0; i < boneCount; i++)
        {
            var offset = bonesOffset + i * 132;
            var boneName = ReadName(data, offset, 64);
            var matrix = new float[16];
            for (var j = 0; j < 16; j++) matrix[j] = F32(data, offset + 64 + j * 4);
            var parent1Based = U32(data, offset + 128);
            bones.Add(new SkyBone(boneName, parent1Based == 0 ? -1 : checked((int)parent1Based - 1), ToMatrix(matrix)));
        }

        var offsetInPack = bonesOffset + boneCount * 132;
        var clips = new List<SkyAnimationClip>();
        var segmentIndex = 0;
        while (offsetInPack < data.Length)
        {
            SkyPose[]? referencePose = null;
            if (refSqtFlag > 0 && boneDefsFlag > 0)
            {
                var referenceSize = checked(boneCount * 40);
                if (offsetInPack + referenceSize > data.Length) break;
                referencePose = new SkyPose[boneCount];
                for (var i = 0; i < boneCount; i++) referencePose[i] = ReadPose(data, offsetInPack + i * 40);
                offsetInPack += referenceSize;
            }

            byte[] clipData;
            if (compression > 0)
            {
                if (offsetInPack + 8 > data.Length) break;
                var compressedSize = checked((int)U32(data, offsetInPack));
                var decompressedSize = checked((int)U32(data, offsetInPack + 4));
                offsetInPack += 8;
                if (compressedSize <= 0 || decompressedSize <= 0 || offsetInPack + compressedSize > data.Length)
                    throw new InvalidDataException($"Animpack segment {segmentIndex + 1} has an invalid compressed block.");
                clipData = new byte[decompressedSize];
                var decoded = LZ4Codec.Decode(data, offsetInPack, compressedSize, clipData, 0, decompressedSize);
                if (decoded != decompressedSize)
                    throw new InvalidDataException($"Animpack segment {segmentIndex + 1} did not decompress completely ({decoded}/{decompressedSize}).");
                offsetInPack += compressedSize;
            }
            else
            {
                clipData = data.AsSpan(offsetInPack).ToArray();
                offsetInPack = data.Length;
            }

            var segmentClips = ParseSegment(name, segmentIndex, version, compression, bones, clipData, referencePose);
            clips.AddRange(segmentClips);
            segmentIndex += segmentClips.Count;
        }
        if (!string.IsNullOrWhiteSpace(animationListPath) && File.Exists(animationListPath))
            ApplyAnimationNames(clips, ReadAnimationNames(animationListPath, name));
        return clips;
    }

    private static IReadOnlyList<SkyAnimationClip> ParseSegment(string packName, int firstClipIndex, uint version, byte compression, IReadOnlyList<SkyBone> bones, byte[] data, SkyPose[]? referencePose)
    {
        var reader = new BufferReader(data);
        var headerWords = version >= 10 ? 6 : 5;
        var keyframeSetCount = checked((int)reader.ReadUInt32());
        for (var i = 1; i < headerWords; i++) reader.ReadUInt32();
        var basePose = new SkyPose[bones.Count];
        for (var i = 0; i < basePose.Length; i++) basePose[i] = reader.ReadPose();
        if (referencePose is { Length: > 0 }) basePose = referencePose;
        var sets = new List<KeyframeSet>(Math.Clamp(keyframeSetCount, 0, 10000));
        for (var i = 0; i < keyframeSetCount; i++)
        {
            if (reader.Remaining == 0) break;
            sets.Add(ReadKeyframeSet(ref reader, bones.Count, version, compression));
        }

        var result = new List<SkyAnimationClip>(sets.Count);
        for (var clipIndex = 0; clipIndex < sets.Count; clipIndex++)
        {
            var set = sets[clipIndex];
            if (set.EndFrame < set.StartFrame || set.FrameCount is < 1 or > 20000)
                throw new InvalidDataException($"Animpack clip {clipIndex + 1} has an invalid frame range.");
            var startPose = (SkyPose[])basePose.Clone();
            for (var bone = 0; bone < bones.Count; bone++)
            {
                var current = startPose[bone];
                var channel = set.Bones[bone];
                startPose[bone] = current with
                {
                    Scale = channel.InitialScale ?? current.Scale,
                    Rotation = channel.InitialRotation ?? current.Rotation,
                    Translation = channel.InitialTranslation ?? current.Translation,
                };
            }

            // Each keyframe set is an independent clip on the pack's shared frame timeline.
            var frames = Enumerable.Range(0, set.FrameCount).Select(_ => (SkyPose[])startPose.Clone()).ToArray();
            for (var frame = 0; frame < set.FrameCount; frame++)
            {
                for (var bone = 0; bone < bones.Count; bone++)
                {
                    var values = set.Bones[bone];
                    var pose = frames[frame][bone];
                    frames[frame][bone] = pose with
                    {
                        Scale = Get(values.Scales, frame, pose.Scale),
                        Rotation = Get(values.Rotations, frame, pose.Rotation),
                        Translation = Get(values.Translations, frame, pose.Translation),
                    };
                }
            }
            result.Add(new SkyAnimationClip($"{packName} · {firstClipIndex + clipIndex + 1}", bones, frames, set.StartFrame, set.EndFrame));
        }
        return result;
    }

    private static IReadOnlyList<(string Name, int Start, int End)> ReadAnimationNames(string path, string packName)
    {
        var data = File.ReadAllBytes(path);
        var nameBytes = Encoding.UTF8.GetBytes(packName);
        var key = new byte[4 + nameBytes.Length];
        BitConverter.GetBytes(nameBytes.Length).CopyTo(key, 0);
        nameBytes.CopyTo(key, 4);
        var found = data.AsSpan().IndexOf(key);
        if (found < 0 || found + key.Length + 4 > data.Length) return [];

        var rawCount = U32(data, found + key.Length);
        if (rawCount > 10000) return [];
        var count = (int)rawCount;
        var result = new List<(string Name, int Start, int End)>(Math.Min(count, 512));
        var offset = found + key.Length + 4;
        while (result.Count < count && offset < data.Length - 32)
        {
            var rawLength = U32(data, offset);
            if (rawLength is >= 1 and <= 63 && offset + 4 + rawLength + 16 <= data.Length)
            {
                var length = (int)rawLength;
                var nameSpan = data.AsSpan(offset + 4, length);
                var printable = true;
                foreach (var value in nameSpan)
                    if (value is < 32 or >= 127) { printable = false; break; }
                if (printable)
                {
                    var start = U32(data, offset + 4 + length);
                    var end = U32(data, offset + 8 + length);
                    var format = U32(data, offset + 12 + length);
                    var speed = F32(data, offset + 16 + length);
                    if (start <= end && end < 200000 && format <= 3 && Math.Abs(speed) is > 0.01f and < 100f)
                    {
                        result.Add((Encoding.ASCII.GetString(nameSpan), (int)start, (int)end));
                        offset += 4 + length + 16;
                        continue;
                    }
                }
            }
            offset++;
        }
        return result;
    }

    private static void ApplyAnimationNames(List<SkyAnimationClip> clips, IReadOnlyList<(string Name, int Start, int End)> entries)
    {
        var namesByRange = new Dictionary<(int Start, int End), string>();
        foreach (var entry in entries) namesByRange.TryAdd((entry.Start - 1, entry.End - 1), entry.Name);
        for (var i = 0; i < clips.Count; i++)
        {
            var clip = clips[i];
            if (namesByRange.TryGetValue((clip.StartFrame, clip.EndFrame), out var name))
                clips[i] = clip with { Name = name };
        }
    }

    private static KeyframeSet ReadKeyframeSet(ref BufferReader reader, int boneCount, uint version, byte compression)
    {
        var start = checked((int)reader.ReadUInt32());
        var end = checked((int)reader.ReadUInt32());
        var flags = reader.ReadUInt32();
        if (version > 8) { reader.Skip(24); }
        var extra1 = Vector3.Zero;
        var extra2 = Vector3.Zero;
        if (version >= 11) { extra1 = reader.ReadVector3(); extra2 = reader.ReadVector3(); }
        var perBoneFlags = reader.ReadBytes(boneCount);
        var frameCount = end >= start ? checked(end - start + 1) : 0;
        if (frameCount > 20000) throw new InvalidDataException("Animpack keyframe range is too large.");
        var channels = Enumerable.Range(0, boneCount).Select(_ => new BoneChannels()).ToArray();
        var quatSize = compression == 2 ? 8 : 16;
        var transSize = compression == 2 && (flags & 1) != 0 ? 6 : 12;

        // Constant channel flags (bits 3-5) supply values for the whole clip;
        // animated channel flags (bits 0-2) carry one value per frame.
        for (var i = 0; i < boneCount; i++) if ((perBoneFlags[i] & (1 << 3)) != 0) channels[i].InitialScale = reader.ReadVector3();
        for (var i = 0; i < boneCount; i++) if ((perBoneFlags[i] & (1 << 4)) != 0) channels[i].InitialRotation = ReadQuaternion(ref reader, compression);
        for (var i = 0; i < boneCount; i++) if ((perBoneFlags[i] & (1 << 5)) != 0) channels[i].InitialTranslation = ReadTranslation(ref reader, compression, (flags & 1) != 0, extra1, extra2);

        for (var frame = 0; frame < frameCount; frame++)
        {
            for (var i = 0; i < boneCount; i++) if ((perBoneFlags[i] & 1) != 0) channels[i].Scales.Add(reader.ReadVector3());
            for (var i = 0; i < boneCount; i++) if ((perBoneFlags[i] & 2) != 0) channels[i].Rotations.Add(ReadQuaternion(ref reader, compression));
            for (var i = 0; i < boneCount; i++) if ((perBoneFlags[i] & 4) != 0) channels[i].Translations.Add(ReadTranslation(ref reader, compression, (flags & 1) != 0, extra1, extra2));
        }
        return new KeyframeSet(start, end, frameCount, channels);
    }

    private static Vector3 ReadTranslation(ref BufferReader reader, byte compression, bool packed, Vector3 offset, Vector3 scale)
    {
        if (compression != 2 || !packed) return reader.ReadVector3();
        var values = reader.ReadUInt16Vector3();
        return offset + new Vector3(values.X / 65535f * scale.X, values.Y / 65535f * scale.Y, values.Z / 65535f * scale.Z);
    }

    private static Quaternion ReadQuaternion(ref BufferReader reader, byte compression)
    {
        var value = compression == 2 ? reader.ReadUInt16Quaternion() : reader.ReadQuaternion();
        return value.LengthSquared() > 1e-10f ? Quaternion.Normalize(value) : Quaternion.Identity;
    }

    private static Vector3 Get(List<Vector3> values, int index, Vector3 fallback) => index < values.Count ? values[index] : fallback;
    private static Quaternion Get(List<Quaternion> values, int index, Quaternion fallback) => index < values.Count ? values[index] : fallback;

    private static Matrix4x4 ToMatrix(float[] values) => new(
        values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7],
        values[8], values[9], values[10], values[11], values[12], values[13], values[14], values[15]);

    private static SkyPose ReadPose(byte[] data, int offset)
    {
        Ensure(data, offset, 40);
        var scale = new Vector3(F32(data, offset), F32(data, offset + 4), F32(data, offset + 8));
        var rotation = new Quaternion(F32(data, offset + 12), F32(data, offset + 16), F32(data, offset + 20), F32(data, offset + 24));
        if (rotation.LengthSquared() > 1e-10f) rotation = Quaternion.Normalize(rotation);
        else rotation = Quaternion.Identity;
        var translation = new Vector3(F32(data, offset + 28), F32(data, offset + 32), F32(data, offset + 36));
        return new SkyPose(scale, rotation, translation);
    }

    private static string ReadName(byte[] data, int offset, int length)
    {
        Ensure(data, offset, length);
        var span = data.AsSpan(offset, length);
        var terminator = span.IndexOf((byte)0);
        return Encoding.UTF8.GetString(terminator >= 0 ? span[..terminator] : span);
    }

    private static uint U32(byte[] data, int offset) { Ensure(data, offset, 4); return BitConverter.ToUInt32(data, offset); }
    private static float F32(byte[] data, int offset) { Ensure(data, offset, 4); return BitConverter.ToSingle(data, offset); }
    private static void Ensure(byte[] data, int offset, int size)
    {
        if (offset < 0 || size < 0 || offset > data.Length - size) throw new InvalidDataException("Animpack data ended unexpectedly.");
    }

    private sealed class BoneChannels
    {
        public Vector3? InitialScale { get; set; }
        public Quaternion? InitialRotation { get; set; }
        public Vector3? InitialTranslation { get; set; }
        public List<Vector3> Scales { get; } = [];
        public List<Quaternion> Rotations { get; } = [];
        public List<Vector3> Translations { get; } = [];
    }
    private sealed record KeyframeSet(int StartFrame, int EndFrame, int FrameCount, BoneChannels[] Bones);

    private ref struct BufferReader(byte[] data)
    {
        private int _offset;
        public readonly int Remaining => data.Length - _offset;
        public uint ReadUInt32() { Ensure(data, _offset, 4); var value = BitConverter.ToUInt32(data, _offset); _offset += 4; return value; }
        public ushort ReadUInt16() { Ensure(data, _offset, 2); var value = BitConverter.ToUInt16(data, _offset); _offset += 2; return value; }
        public float ReadSingle() { Ensure(data, _offset, 4); var value = BitConverter.ToSingle(data, _offset); _offset += 4; return value; }
        public void Skip(int count) { Ensure(data, _offset, count); _offset += count; }
        public byte[] ReadBytes(int count) { Ensure(data, _offset, count); var value = data.AsSpan(_offset, count).ToArray(); _offset += count; return value; }
        public Vector3 ReadVector3() => new(ReadSingle(), ReadSingle(), ReadSingle());
        public Quaternion ReadQuaternion() => new(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());
        public Vector3 ReadUInt16Vector3() => new(ReadUInt16(), ReadUInt16(), ReadUInt16());
        public Quaternion ReadUInt16Quaternion()
        {
            var x = (ReadUInt16() - 32768) / 32767f; var y = (ReadUInt16() - 32768) / 32767f;
            var z = (ReadUInt16() - 32768) / 32767f; var w = (ReadUInt16() - 32768) / 32767f;
            return new Quaternion(x, y, z, w);
        }
        public SkyPose ReadPose()
        {
            var scale = ReadVector3();
            var rotation = ReadQuaternion();
            if (rotation.LengthSquared() > 1e-10f) rotation = Quaternion.Normalize(rotation);
            else rotation = Quaternion.Identity;
            return new SkyPose(scale, rotation, ReadVector3());
        }
    }
}
