using System.Collections.Concurrent;
using System.IO;
using System.Text.RegularExpressions;
using SPIRVCross.NET;
using SharpDX.D3DCompiler;
using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Shaders;
using SkyModel.Core.Resources;

namespace SkyCharacterStudio;

internal sealed record GameAvatarShaderProgram(
    string ShaderName,
    string VertexSource,
    string FragmentSource,
    byte[] VertexBytecode,
    byte[] FragmentBytecode,
    SharpDX.Direct3D11.InputElement[] VertexInputElements,
    IReadOnlyList<GameShaderUniformBlock> UniformBlocks,
    IReadOnlyList<GameShaderBinding> Bindings,
    string ShaderGroup,
    string AssetDirectory);

/// <summary>
/// Loads the game's compiled Avatar SPIR-V stages and cross-compiles them to
/// Direct3D 11 HLSL and then native DXBC. The generated stages are ready to be
/// registered as a Direct3D shader pass in the custom viewport pipeline.
/// </summary>
internal static class GameAvatarShaderCompiler
{
    private static readonly ConcurrentDictionary<string, GameAvatarShaderProgram> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static GameAvatarShaderProgram LoadForMesh(string meshPath, string shaderName)
    {
        if (string.IsNullOrWhiteSpace(meshPath))
            throw new ArgumentException("A mesh path is required to locate the game's asset package.", nameof(meshPath));

        shaderName = string.IsNullOrWhiteSpace(shaderName) ? "Avatar" : shaderName.Trim();
        var assetDirectory = FindShaderDirectory(meshPath, shaderName);
        var shaderGroup = SkyResourceResolver.FindShaderGroup(meshPath, shaderName) ?? string.Empty;
        var key = Path.Combine(assetDirectory, shaderName + "-13h.win");
        return Cache.GetOrAdd(key, _ => Compile(assetDirectory, shaderName, shaderGroup));
    }

    private static GameAvatarShaderProgram Compile(string assetDirectory, string shaderName, string shaderGroup)
    {
        var vertexPath = Path.Combine(assetDirectory, $"{shaderName}-13h.win.vs.spv");
        var fragmentPath = Path.Combine(assetDirectory, $"{shaderName}-13h.win.fs.spv");
        var reflectionPath = Path.Combine(assetDirectory, $"{shaderName}-13h.win.ref");
        if (!File.Exists(vertexPath) || !File.Exists(fragmentPath))
            throw new FileNotFoundException($"Game shader stages were not found for '{shaderName}' (13h/win).");
        if (!File.Exists(reflectionPath))
            throw new FileNotFoundException($"Game shader reflection was not found for '{shaderName}' (13h/win).");

        using var context = new Context();
        var vertexCompiler = context.CreateHLSLCompiler(File.ReadAllBytes(vertexPath));
        vertexCompiler.hlslOptions.shaderModel = 50;
        var vertexSource = vertexCompiler.Compile();

        var fragmentCompiler = context.CreateHLSLCompiler(File.ReadAllBytes(fragmentPath));
        fragmentCompiler.hlslOptions.shaderModel = 50;
        var fragmentSource = fragmentCompiler.Compile();

        var helixVertexSource = AdaptVertexInputLayout(vertexSource);
        var vertexStageSource = RenameStageBuffers(helixVertexSource, shaderName, "Vertex");
        var fragmentStageSource = RenameStageBuffers(fragmentSource, shaderName, "Fragment");
        if (shaderName.StartsWith("Avatar", StringComparison.OrdinalIgnoreCase))
            fragmentStageSource = ApplyAvatarDisplayTransform(fragmentStageSource);
        var vertexBytecode = CompileHlsl(vertexStageSource, "vs_5_0", vertexPath);
        var fragmentBytecode = CompileHlsl(fragmentStageSource, "ps_5_0", fragmentPath);

        var bindings = ReadBindings(vertexBytecode, fragmentBytecode);
        var textureSlots = GameAvatarShaderReflection.ReadTextureSlots(reflectionPath);
        bindings = bindings.Select(binding =>
        {
            if (binding.ResourceType.Contains("texture", StringComparison.OrdinalIgnoreCase)
                && binding.Stage.Equals("Fragment", StringComparison.OrdinalIgnoreCase)
                && textureSlots.TryGetValue(binding.Slot, out var resourceName))
                return binding with { Name = resourceName };
            return binding;
        }).ToList();

        var d3dUniformBlocks = ReadD3dUniformBlocks(vertexBytecode, fragmentBytecode, bindings);
        var reflectedBlocks = GameAvatarShaderReflection.Read(reflectionPath);
        var uniformBlocks = new List<GameShaderUniformBlock>(reflectedBlocks.Count);
        foreach (var reflected in reflectedBlocks)
        {
            var compiled = d3dUniformBlocks.FirstOrDefault(block =>
                block.Stage.Equals(reflected.Stage, StringComparison.OrdinalIgnoreCase)
                && block.Register == reflected.Register);
            if (compiled is null)
                throw new InvalidDataException($"Game .ref uniform block {reflected.Stage}.{reflected.Name} is missing from the compiled Avatar stage.");
            foreach (var expected in reflected.Fields)
            {
                if (expected.Offset < 0 || expected.Offset + expected.Size > Math.Max(compiled.ByteSize, reflected.ByteSize))
                    throw new InvalidDataException($"Game .ref field {reflected.Stage}.{expected.Name} exceeds its compiled constant buffer.");
            }
            // SPIR-V strips the original member and resource names. Keep the
            // DXBC buffer identity for Helix binding, but restore the game's
            // semantic member names and offsets from the adjacent .ref file.
            uniformBlocks.Add(new GameShaderUniformBlock(
                compiled.Name,
                reflected.Stage,
                compiled.Register,
                Math.Max(compiled.ByteSize, reflected.ByteSize),
                reflected.Fields));
        }
        return new GameAvatarShaderProgram(shaderName, vertexStageSource, fragmentStageSource, vertexBytecode, fragmentBytecode, CreateHelixInputLayout(), uniformBlocks, bindings, shaderGroup, assetDirectory);
    }

    private static string ApplyAvatarDisplayTransform(string source)
    {
        // The game Avatar shader produces linear HDR scene color. The guide's
        // reference renderer runs Tonemap_rec709_sRGB at the end of the frame;
        // this viewport has no scene-wide post pass, so apply its essential
        // highlight shoulder and sRGB output transfer at the Avatar fragment.
        var assignment = Regex.Match(source,
            @"(?m)^(?<indent>\s*)stage_output\.(?<field>\w+)\s*=\s*(?<value>\w+);\s*$");
        if (!assignment.Success) return source;
        const string helper = """
            float3 SkyStudioToneMapLinearToSrgb(float3 linearColor)
            {
                float3 exposed = max(linearColor, 0.0f) * 0.65f;
                float3 mapped = saturate((exposed * (2.51f * exposed + 0.03f)) / (exposed * (2.43f * exposed + 0.59f) + 0.14f));
                float3 low = mapped * 12.92f;
                float3 high = 1.055f * pow(mapped, 1.0f / 2.4f) - 0.055f;
                return lerp(high, low, step(mapped, 0.0031308f));
            }

            """;
        var value = assignment.Groups["value"].Value;
        var replacement = $"{assignment.Groups["indent"].Value}stage_output.{assignment.Groups["field"].Value} = float4(SkyStudioToneMapLinearToSrgb({value}.rgb), {value}.a);";
        return helper + Regex.Replace(source, Regex.Escape(assignment.Value), _ => replacement, RegexOptions.None, TimeSpan.FromSeconds(1));
    }

    private static string RenameStageBuffers(string source, string shaderName, string stage)
    {
        var safeShaderName = Regex.Replace(shaderName, "[^A-Za-z0-9_]", "_");
        var prefix = $"Sky_{safeShaderName}_{stage}";
        var suffix = 0;
        return Regex.Replace(source, @"(?m)^cbuffer\s+\w+\s*:\s*register\(b(\d+)\)", match =>
        {
            var slot = match.Groups[1].Value;
            return $"cbuffer {prefix}_b{slot}_{suffix++} : register(b{slot})";
        });
    }

    private static IReadOnlyList<GameShaderUniformBlock> ReadD3dUniformBlocks(
        byte[] vertexBytecode,
        byte[] fragmentBytecode,
        IReadOnlyList<GameShaderBinding> bindings)
    {
        var blocks = new List<GameShaderUniformBlock>();
        AddStage(vertexBytecode, "Vertex");
        AddStage(fragmentBytecode, "Fragment");
        return blocks;

        void AddStage(byte[] bytecode, string stage)
        {
            using var reflection = new ShaderReflection(bytecode);
            var description = reflection.Description;
            for (var i = 0; i < description.ConstantBuffers; i++)
            {
                var buffer = reflection.GetConstantBuffer(i);
                var bufferDescription = buffer.Description;
                var binding = bindings.FirstOrDefault(item => item.Stage.Equals(stage, StringComparison.OrdinalIgnoreCase)
                    && item.Name.Equals(bufferDescription.Name, StringComparison.OrdinalIgnoreCase)
                    && item.ResourceType.Equals("constant buffer", StringComparison.OrdinalIgnoreCase));
                if (binding is null) continue;

                var fields = new List<GameShaderUniformField>();
                for (var variableIndex = 0; variableIndex < bufferDescription.VariableCount; variableIndex++)
                {
                    var variable = buffer.GetVariable(variableIndex);
                    var variableDescription = variable.Description;
                    var typeDescription = variable.GetVariableType().Description;
                    var type = MapUniformType(typeDescription);
                    if (type == 0 || !variableDescription.Name.StartsWith("u_", StringComparison.Ordinal)) continue;
                    fields.Add(new GameShaderUniformField(
                        variableDescription.Name,
                        type,
                        Math.Max(1, typeDescription.ElementCount),
                        variableDescription.StartOffset,
                        variableDescription.Size));
                }

                blocks.Add(new GameShaderUniformBlock(bufferDescription.Name, stage, binding.Slot, bufferDescription.Size, fields));
            }
        }
    }

    private static int MapUniformType(ShaderTypeDescription description)
    {
        if (description.Class is ShaderVariableClass.MatrixRows or ShaderVariableClass.MatrixColumns)
            return description.RowCount == 4 && description.ColumnCount == 4 ? 0x23 : 0;
        if (description.Class is not (ShaderVariableClass.Scalar or ShaderVariableClass.Vector)) return 0;
        if (description.Type is ShaderVariableType.Int or ShaderVariableType.UInt or ShaderVariableType.Bool) return 9;
        if (description.Type != ShaderVariableType.Float) return 0;
        return description.ColumnCount switch { 1 => 1, 2 => 2, 3 => 3, 4 => 4, _ => 0 };
    }

    private static IReadOnlyList<GameShaderBinding> ReadBindings(byte[] vertexBytecode, byte[] fragmentBytecode)
    {
        var bindings = new List<GameShaderBinding>();
        AddBindings(vertexBytecode, ShaderStage.Vertex, "Vertex", bindings);
        AddBindings(fragmentBytecode, ShaderStage.Pixel, "Fragment", bindings);
        return bindings;
    }

    private static void AddBindings(byte[] bytecode, ShaderStage stage, string stageName, ICollection<GameShaderBinding> bindings)
    {
        var reflector = new ShaderReflector();
        reflector.Parse(bytecode, stage);
        foreach (var mapping in reflector.ConstantBufferMappings.Values)
        {
            var buffer = mapping.Description;
            bindings.Add(new GameShaderBinding(buffer.Name, stageName, mapping.Slot, "constant buffer", buffer.StructSize));
        }
        foreach (var mapping in reflector.TextureMappings.Values)
            bindings.Add(new GameShaderBinding(mapping.Description.Name, stageName, mapping.Slot, mapping.Description.Type.ToString()));
        foreach (var mapping in reflector.SamplerMappings.Values)
            bindings.Add(new GameShaderBinding(mapping.Name, stageName, mapping.Slot, "sampler"));
    }

    private static string AdaptVertexInputLayout(string source)
    {
        var inputs = """
            float4 _275 : POSITION;
                float4 _289 : COLOR0;
                float3 _281 : NORMAL;
                float3 _352Packed : TANGENT;
                float3 _355Packed : BINORMAL;
                float2 _uvTail : TEXCOORD0;
            """;
        var expectedInputs = """
                float4 _275 : TEXCOORD0;
                float4 _289 : TEXCOORD1;
                float3 _281 : TEXCOORD2;
                float4 _352 : TEXCOORD3;
                float4 _355 : TEXCOORD4;
            """;
        if (!source.Contains(expectedInputs, StringComparison.Ordinal))
            throw new InvalidDataException("Avatar vertex inputs differ from the known 13h Helix vertex layout.");

        source = source.Replace(expectedInputs, inputs, StringComparison.Ordinal);
        source = source.Replace("_352 = stage_input._352;", "_352 = float4(stage_input._352Packed, stage_input._355Packed.x);", StringComparison.Ordinal);
        source = source.Replace("_355 = stage_input._355;", "_355 = float4(stage_input._355Packed.yz, stage_input._uvTail);", StringComparison.Ordinal);
        if (source.Contains("_352 = stage_input._352;", StringComparison.Ordinal)
            || source.Contains("_355 = stage_input._355;", StringComparison.Ordinal))
            throw new InvalidDataException("Avatar vertex input assignments could not be adapted for Helix.");
        return source;
    }

    private static SharpDX.Direct3D11.InputElement[] CreateHelixInputLayout() =>
    [
        // MeshGeometry3D's default buffer is split into streams: DefaultVertex
        // (POSITION/NORMAL/TANGENT/BINORMAL) is slot 0, texture coordinates are
        // slot 1, and colors are slot 2. Keep explicit offsets because the Sky
        // shader reuses tangent/binormal components as packed UV sets.
        new("POSITION", 0, SharpDX.DXGI.Format.R32G32B32A32_Float, 0, 0),
        new("COLOR", 0, SharpDX.DXGI.Format.R32G32B32A32_Float, 0, 2),
        new("NORMAL", 0, SharpDX.DXGI.Format.R32G32B32_Float, 16, 0),
        new("TANGENT", 0, SharpDX.DXGI.Format.R32G32B32_Float, 28, 0),
        new("BINORMAL", 0, SharpDX.DXGI.Format.R32G32B32_Float, 40, 0),
        new("TEXCOORD", 0, SharpDX.DXGI.Format.R32G32_Float, 0, 1),
    ];

    private static byte[] CompileHlsl(string source, string profile, string sourceName)
    {
        using var result = ShaderBytecode.Compile(
            source,
            "main",
            profile,
            ShaderFlags.OptimizationLevel3,
            EffectFlags.None,
            sourceName);
        return result.Bytecode.Data;
    }

    private static string FindShaderDirectory(string meshPath, string shaderName)
    {
        var cursor = Path.GetDirectoryName(Path.GetFullPath(meshPath));
        string? assetsRoot = null;
        while (!string.IsNullOrEmpty(cursor))
        {
            if (Path.GetFileName(cursor).Equals("assets", StringComparison.OrdinalIgnoreCase))
            {
                assetsRoot = cursor;
                break;
            }
            cursor = Path.GetDirectoryName(cursor);
        }

        if (assetsRoot is null)
            throw new DirectoryNotFoundException("The selected mesh is not inside a Sky assets package.");

        var relativeMeshPath = Path.GetRelativePath(assetsRoot, Path.GetFullPath(meshPath));
        var packageName = relativeMeshPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        var packages = new[] { packageName, "initial" }
            .Concat(Directory.EnumerateDirectories(assetsRoot).Select(path => Path.GetFileName(path)).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var package in packages)
        {
            if (string.IsNullOrWhiteSpace(package)) continue;
            var directory = Path.Combine(assetsRoot, package, "Data", "Shaders", "Bin");
            if (File.Exists(Path.Combine(directory, $"{shaderName}-13h.win.vs.spv"))
                && File.Exists(Path.Combine(directory, $"{shaderName}-13h.win.fs.spv")))
                return directory;
        }

        throw new FileNotFoundException($"Could not find the game's 13h Windows shader '{shaderName}' under '{assetsRoot}'.");
    }
}
