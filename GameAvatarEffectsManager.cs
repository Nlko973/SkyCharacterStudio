using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Shaders;
using SharpDX.Direct3D11;

namespace SkyCharacterStudio;

/// <summary>Registers the converted Sky Avatar stages as a real Helix technique.</summary>
internal sealed class GameAvatarEffectsManager : DefaultEffectsManager
{
    // MeshGeometryModel3D resolves its render technique through
    // DefaultRenderTechniqueNames.Mesh ("RenderMesh"). Replacing RenderOpaque
    // leaves the built-in mesh technique active, so the custom Sky material pass
    // is never selected and the scene silently looks like the old preview.
    public const string TechniqueName = "RenderMesh";
    public static string PassName(string shaderName) => $"SkyGameAvatar_{shaderName}";

    public GameAvatarEffectsManager(IReadOnlyList<GameAvatarShaderProgram> programs)
    {
        if (programs.Count == 0) throw new ArgumentException("At least one Avatar program is required.", nameof(programs));
        RemoveTechnique(TechniqueName);
        InputLayoutDescription? techniqueLayout = null;
        var passes = new List<ShaderPassDescription>();
        foreach (var program in programs)
        {
            var vertexReflector = new ShaderReflector();
            vertexReflector.Parse(program.VertexBytecode, ShaderStage.Vertex);
            var pixelReflector = new ShaderReflector();
            pixelReflector.Parse(program.FragmentBytecode, ShaderStage.Pixel);
            var vertex = new ShaderDescription($"SkyGameAvatarVS_{program.ShaderName}", ShaderStage.Vertex, vertexReflector, program.VertexBytecode);
            var pixel = new ShaderDescription($"SkyGameAvatarPS_{program.ShaderName}", ShaderStage.Pixel, pixelReflector, program.FragmentBytecode);
            var layout = new InputLayoutDescription(program.VertexBytecode, program.VertexInputElements);
            techniqueLayout ??= layout;
            passes.Add(new ShaderPassDescription(PassName(program.ShaderName))
            {
                InputLayoutDescription = layout,
                ShaderList = [vertex, pixel],
                // ShaderDefs.lua has several blended Avatar variants whose
                // names do not contain "Alpha" (Glass, Butterfly, ElderMask).
                // AlphaTest variants instead stay opaque and rely on discard.
                BlendStateDescription = IsBlendedAvatarShader(program)
                    ? CreateAlphaBlendState()
                    : null,
                DepthStencilStateDescription = IsBlendedAvatarShader(program)
                    ? CreateTransparentDepthState(program.ShaderGroup)
                    : null,
            });
        }
        AddTechnique(new TechniqueDescription(TechniqueName, techniqueLayout!, passes));
    }

    internal static bool IsBlendedAvatarShader(GameAvatarShaderProgram program) =>
        (!string.IsNullOrWhiteSpace(program.ShaderGroup)
            ? program.ShaderGroup.StartsWith("Blended", StringComparison.OrdinalIgnoreCase)
            : program.ShaderName is
                "AvatarScrollAlpha" or "AvatarAlpha" or "AvatarGlass" or
                "AvatarChamAlpha" or "AvatarChamElderMask" or "AvatarChamGlitterAlpha" or
                "AvatarChamAlphaFullGlitter" or "AvatarChamButterfly" or "AvatarChamOceanCaustics" or
                "AvatarOceanCaustics" or "AvatarOceanCausticsClipped" or "AvatarChamGlass" or
                "AvatarChamBlueTear" or "AvatarChamGlitterAlphaRevamp" or "AvatarChamGlitterAlphaAttrib");

    private static BlendStateDescription CreateAlphaBlendState()
    {
        var state = BlendStateDescription.Default();
        state.RenderTarget[0].IsBlendEnabled = true;
        state.RenderTarget[0].SourceBlend = BlendOption.SourceAlpha;
        state.RenderTarget[0].DestinationBlend = BlendOption.InverseSourceAlpha;
        state.RenderTarget[0].BlendOperation = BlendOperation.Add;
        state.RenderTarget[0].SourceAlphaBlend = BlendOption.One;
        state.RenderTarget[0].DestinationAlphaBlend = BlendOption.InverseSourceAlpha;
        state.RenderTarget[0].AlphaBlendOperation = BlendOperation.Add;
        return state;
    }

    private static DepthStencilStateDescription CreateTransparentDepthState(string shaderGroup)
    {
        var state = DepthStencilStateDescription.Default();
        state.IsDepthEnabled = true;
        // Sky's BlendedDepth queue is transparent but retains depth writes.
        state.DepthWriteMask = shaderGroup.Equals("BlendedDepth", StringComparison.OrdinalIgnoreCase)
            ? DepthWriteMask.All
            : DepthWriteMask.Zero;
        state.DepthComparison = Comparison.LessEqual;
        return state;
    }
}
