using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Model;
using HelixToolkit.SharpDX.Render;
using HelixToolkit.SharpDX.Shaders;
using HelixToolkit.SharpDX.Utilities;
using SkyModel.Core.Models;
using System.Numerics;
using SharpDX.Direct3D11;

namespace SkyCharacterStudio;

internal sealed class GameAvatarMaterial(GameAvatarMaterialCore core) : HelixToolkit.Wpf.SharpDX.Material(core)
{
    protected override MaterialCore OnCreateCore() => core;
    protected override System.Windows.Freezable CreateInstanceCore() => new GameAvatarMaterial(core);
}

/// <summary>Material core that selects the registered game Avatar shader pass.</summary>
internal sealed class GameAvatarMaterialCore : MaterialCore
{
    private readonly GameAvatarShaderProgram _program;
    private readonly Dictionary<string, Vector4> _values;
    private readonly IReadOnlyDictionary<string, Matrix4x4> _matrices;
    private readonly IReadOnlyDictionary<string, TextureModel> _textures;
    public event Action? MatricesChanged;
    public event Action? ValuesChanged;

    public GameAvatarMaterialCore(
        GameAvatarShaderProgram program,
        IReadOnlyDictionary<string, Vector4>? values = null,
        IReadOnlyDictionary<string, Matrix4x4>? matrices = null,
        IReadOnlyDictionary<string, TextureModel>? textures = null)
    {
        _program = program;
        _values = values is null
            ? new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
            : values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        _matrices = matrices ?? new Dictionary<string, Matrix4x4>();
        _textures = textures ?? new Dictionary<string, TextureModel>();
    }

    public override MaterialVariable CreateMaterialVariables(IEffectsManager manager, IRenderTechnique technique) =>
        new GameAvatarMaterialVariable(manager, technique, this, CreateUniformBuffers(), _program, _textures);

    internal IReadOnlyList<GameAvatarUniformBuffer> CreateUniformBuffers() =>
        GameAvatarUniformBuffers.Create(_program, _values, _matrices);

    public void UpdateMatrices(IReadOnlyDictionary<string, Matrix4x4> matrices)
    {
        if (_matrices is not Dictionary<string, Matrix4x4> target) return;
        target.Clear();
        foreach (var pair in matrices) target[pair.Key] = pair.Value;
        MatricesChanged?.Invoke();
    }

    public void UpdateAnimatedValues(float timeSeconds, float deltaSeconds)
    {
        _values["u_time"] = new Vector4(timeSeconds);
        _values["u_levelTime"] = new Vector4(timeSeconds);
        _values["u_dt"] = new Vector4(deltaSeconds);
        _values["u_averagedt"] = new Vector4(deltaSeconds);
        ValuesChanged?.Invoke();
    }

    public void UpdateValues(IReadOnlyDictionary<string, Vector4> values)
    {
        foreach (var pair in values) _values[pair.Key] = pair.Value;
        ValuesChanged?.Invoke();
    }
}

/// <summary>Uploads reflected per-stage buffers while Helix renders a game-avatar mesh.</summary>
internal sealed class GameAvatarMaterialVariable : MaterialVariable
{
    internal static int ResourceBindCount;
    internal static int DrawCallCount;
    internal static int MaterialVariableCount;
    private readonly ShaderPass _pass;
    private readonly GameAvatarConstantBufferSet _constantBuffers;
    private readonly IReadOnlyList<(GameShaderBinding Binding, ShaderResourceViewProxy View)> _textures;
    private readonly IReadOnlyList<(GameShaderBinding Binding, SamplerStateProxy State)> _samplers;
    private readonly GameAvatarMaterialCore _material;
    private readonly GameAvatarShaderProgram _program;
    private readonly ShaderResourceView? _neutralProbeCube;

    public GameAvatarMaterialVariable(
        IEffectsManager manager,
        IRenderTechnique technique,
        GameAvatarMaterialCore material,
        IReadOnlyList<GameAvatarUniformBuffer> buffers,
        GameAvatarShaderProgram program,
        IReadOnlyDictionary<string, TextureModel> textures)
        // MeshRenderCore calls UpdateMaterialStruct before binding any material
        // resources. A null mesh-buffer description makes that base method
        // return false, so Helix silently skips every draw for this custom pass.
        // The game shader does not consume this Helix buffer; it only satisfies
        // the core's readiness contract while the reflected Sky buffers below
        // carry the actual object and frame uniforms.
        : base(manager, technique, DefaultMeshConstantBufferDesc, material)
    {
        _material = material;
        Interlocked.Increment(ref MaterialVariableCount);
        _program = program;
        _samplers = CreateSamplers(manager, program);
        if (program.Bindings.Any(binding => binding.Name.Equals("u_probeCube", StringComparison.OrdinalIgnoreCase))
            && manager is EffectsManager effectsManager && effectsManager.Device is { } device)
            _neutralProbeCube = NeutralProbeCubeCache.Get(device);
        var passName = GameAvatarEffectsManager.PassName(program.ShaderName);
        _pass = technique.GetPass(passName)
            ?? throw new InvalidOperationException($"Technique is missing pass {passName}.");
        _constantBuffers = new GameAvatarConstantBufferSet(manager, buffers);
        var textureManager = manager.MaterialTextureManager
            ?? throw new InvalidOperationException("Helix material texture manager is not initialized.");
        var boundTextures = new List<(GameShaderBinding Binding, ShaderResourceViewProxy View)>();
        foreach (var binding in program.Bindings.Where(binding => binding.ResourceType.Contains("texture", StringComparison.OrdinalIgnoreCase)))
        {
            if (!textures.TryGetValue(binding.Name, out var texture)) continue;
            var view = textureManager.Register(texture);
            if (view is not null) boundTextures.Add((binding, view));
        }
        _textures = boundTextures;
        _material.MatricesChanged += Material_MatricesChanged;
        _material.ValuesChanged += Material_ValuesChanged;
    }

    private void Material_MatricesChanged() =>
        _constantBuffers.Update(_material.CreateUniformBuffers());

    private void Material_ValuesChanged() =>
        _constantBuffers.Update(_material.CreateUniformBuffers());

    public override bool BindMaterialResources(RenderContext context, DeviceContextProxy deviceContext, ShaderPass shaderPass)
    {
        Interlocked.Increment(ref ResourceBindCount);
        // Use the matrices that Helix actually used for this render target.
        // The side-by-side WPF viewport can be resized or collapsed while a
        // shader load is in flight, so cached WPF dimensions can otherwise
        // produce a different projection and apparent model size.
        var sourceView = context.ViewMatrix;
        var viewMatrix = new Matrix4x4(
            sourceView.M11, sourceView.M12, sourceView.M13, sourceView.M14,
            sourceView.M21, sourceView.M22, sourceView.M23, sourceView.M24,
            sourceView.M31, sourceView.M32, sourceView.M33, sourceView.M34,
            sourceView.M41, sourceView.M42, sourceView.M43, sourceView.M44);
        var sourceProjection = context.ProjectionMatrix;
        var projection = new Matrix4x4(
            sourceProjection.M11, sourceProjection.M12, sourceProjection.M13, sourceProjection.M14,
            sourceProjection.M21, sourceProjection.M22, sourceProjection.M23, sourceProjection.M24,
            sourceProjection.M31, sourceProjection.M32, sourceProjection.M33, sourceProjection.M34,
            sourceProjection.M41, sourceProjection.M42, sourceProjection.M43, sourceProjection.M44);
        var viewProjection = viewMatrix * projection;
        Matrix4x4.Invert(viewProjection, out var inverseViewProjection);
        _material.UpdateMatrices(new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase)
        {
            ["u_model"] = Matrix4x4.Identity,
            ["u_view"] = viewMatrix,
            ["u_proj"] = projection,
            ["u_viewProj"] = viewProjection,
            ["u_viewProjInv"] = inverseViewProjection,
        });
        _constantBuffers.Upload(deviceContext);
        foreach (var (binding, view) in _textures)
        {
            if (binding.Stage.Equals("Vertex", StringComparison.OrdinalIgnoreCase))
                deviceContext.SetShaderResource(HelixToolkit.SharpDX.Shaders.VertexShader.Type, binding.Slot, view.TextureView);
            else if (binding.Stage.Equals("Fragment", StringComparison.OrdinalIgnoreCase))
                deviceContext.SetShaderResource(HelixToolkit.SharpDX.Shaders.PixelShader.Type, binding.Slot, view.TextureView);
        }
        // The official glass shader refracts the opaque scene. Helix renders
        // transparent layers into separate OIT targets, leaving the opaque
        // color buffer available for sampling during either OIT algorithm.
        if (context.OITRenderStage is OITRenderStage.SinglePassWeighted or OITRenderStage.DepthPeeling
            && context.RenderHost.RenderBuffer?.ColorBuffer?.TextureView is { } opaqueColor)
        {
            foreach (var binding in _program.Bindings.Where(binding =>
                         binding.Name.Equals("u_opaqueColor", StringComparison.OrdinalIgnoreCase)
                         && binding.Stage.Equals("Fragment", StringComparison.OrdinalIgnoreCase)))
                deviceContext.SetShaderResource(HelixToolkit.SharpDX.Shaders.PixelShader.Type, binding.Slot, opaqueColor);
        }
        foreach (var (binding, sampler) in _samplers)
        {
            if (binding.Stage.Equals("Vertex", StringComparison.OrdinalIgnoreCase))
                deviceContext.SetSampler(HelixToolkit.SharpDX.Shaders.VertexShader.Type, binding.Slot, sampler.State);
            else if (binding.Stage.Equals("Fragment", StringComparison.OrdinalIgnoreCase))
                deviceContext.SetSampler(HelixToolkit.SharpDX.Shaders.PixelShader.Type, binding.Slot, sampler.State);
        }
        if (_neutralProbeCube is not null)
        {
            foreach (var binding in _program.Bindings.Where(binding => binding.Name.Equals("u_probeCube", StringComparison.OrdinalIgnoreCase)
                         && binding.Stage.Equals("Fragment", StringComparison.OrdinalIgnoreCase)))
                deviceContext.SetShaderResource(HelixToolkit.SharpDX.Shaders.PixelShader.Type, binding.Slot, _neutralProbeCube);
        }
        return true;
    }

    public override ShaderPass GetPass(RenderType renderType, RenderContext context) => _pass;
    public override ShaderPass GetShadowPass(RenderType renderType, RenderContext context) => ShaderPass.NullPass;
    public override ShaderPass GetWireframePass(RenderType renderType, RenderContext context) => _pass;
    public override ShaderPass GetDepthPass(RenderType renderType, RenderContext context) => ShaderPass.NullPass;

    public override void Draw(DeviceContextProxy deviceContext, IAttachableBufferModel bufferModel, int instanceCount)
    {
        var indexCount = bufferModel.IndexBuffer?.ElementCount ?? 0;
        Interlocked.Increment(ref DrawCallCount);
        if (indexCount > 0)
            DrawIndexed(deviceContext, indexCount, instanceCount);
    }

    protected override void OnDispose(bool disposeManagedResources)
    {
        if (disposeManagedResources)
            foreach (var (_, state) in _samplers)
            {
                var sampler = state;
                DisposeObject.RemoveAndDispose(ref sampler);
            }
        base.OnDispose(disposeManagedResources);
    }

    private static IReadOnlyList<(GameShaderBinding Binding, SamplerStateProxy State)> CreateSamplers(
        IEffectsManager manager, GameAvatarShaderProgram program)
    {
        var samplerBindings = program.Bindings
            .Where(binding => binding.ResourceType.Contains("sampler", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (samplerBindings.Length == 0) return [];

        var stateManager = manager.StateManager
            ?? throw new InvalidOperationException("Helix sampler state manager is not initialized.");
        return samplerBindings.Select(binding =>
        {
            var textureName = program.Bindings.FirstOrDefault(candidate =>
                candidate.Stage.Equals(binding.Stage, StringComparison.OrdinalIgnoreCase)
                && candidate.Slot == binding.Slot
                && candidate.ResourceType.Contains("texture", StringComparison.OrdinalIgnoreCase))?.Name;
            var descriptor = textureName switch
            {
                "u_probeCube" => DefaultSamplers.IBLSampler,
                "u_lightPRTex" or "u_lightCSTex" or "u_capeIntegrityNoise" => DefaultSamplers.LinearSamplerClampAni1,
                _ => DefaultSamplers.LinearSamplerWrapAni4,
            };
            var state = stateManager.Register(descriptor)
                ?? throw new InvalidOperationException($"Could not create sampler for {binding.Name} at s{binding.Slot}.");
            return (binding, state);
        }).ToArray();
    }
}
