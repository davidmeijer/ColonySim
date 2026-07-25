using System.Numerics;
using Raylib_cs;
using ColonySim.Entities;
using ColonySim.World;

namespace ColonySim.Rendering;

// The world's single directional light (the sun/moon) plus everything that
// rides along with it: a full day/night cycle (sky colour, light colour and
// strength all drift through the same four keyframes every cycle), and a
// shadow map so trees and hills actually occlude the light instead of every
// surface just shading its own faces in isolation.
public class SunLight
{
  // Shared by both the shadow depth pass and the main lit pass — see
  // RenderShadowMap/BeginLit. vertexPosition is already world space by the
  // time it reaches this shader (raylib's basic shape calls, and DrawModel
  // here with an identity transform, both bake the world transform into
  // the vertex data itself), so fragPosition can just be vertexPosition —
  // no separate model matrix needed anywhere in this shader.
  const string VertexSrc = """
    #version 330
    in vec3 vertexPosition;
    in vec3 vertexNormal;
    in vec4 vertexColor;

    uniform mat4 mvp;

    out vec3 fragPosition;
    out vec3 fragNormal;
    out vec4 fragColor;

    void main()
    {
        fragPosition = vertexPosition;
        fragNormal = vertexNormal;
        fragColor = vertexColor;
        gl_Position = mvp * vec4(vertexPosition, 1.0);
    }
    """;

  // shadowMapTexel is baked in as a literal (rather than a uniform) since
  // the shadow map's resolution never changes at runtime. MAX_POINT_LIGHTS
  // mirrors SunLight.MaxPointLights — see SetPointLights.
  static string FragmentSrc(int shadowMapSize) => $$"""
    #version 330
    in vec3 fragPosition;
    in vec3 fragNormal;
    in vec4 fragColor;

    uniform vec3 lightDir;
    uniform vec3 lightColor;
    uniform vec3 ambientColor;
    uniform mat4 lightVP;
    uniform sampler2D shadowMap;

    #define MAX_POINT_LIGHTS 8
    #define POINT_LIGHT_RADIUS 220.0
    uniform vec3 pointLightPos[MAX_POINT_LIGHTS];
    uniform vec3 pointLightColor[MAX_POINT_LIGHTS];
    uniform int pointLightCount;

    out vec4 finalColor;

    const float shadowMapTexel = 1.0 / {{shadowMapSize}}.0;

    // 3x3 PCF (percentage-closer filtering): averages 9 shadow-map samples
    // instead of just one, so shadow edges fall off softly over a couple of
    // pixels rather than reading as a single hard, aliased line.
    float ShadowFactor(vec3 worldPos, float ndotl)
    {
        vec4 posLightSpace = lightVP * vec4(worldPos, 1.0);
        posLightSpace.xyz /= posLightSpace.w;
        posLightSpace.xyz = posLightSpace.xyz * 0.5 + 0.5;

        // Outside what the shadow map actually covers: nothing known casts
        // a shadow here, so don't darken it.
        if (posLightSpace.z > 1.0 || posLightSpace.x < 0.0 || posLightSpace.x > 1.0 ||
            posLightSpace.y < 0.0 || posLightSpace.y > 1.0) return 1.0;

        // Slope-scaled bias: a surface that's nearly edge-on to the light
        // needs a bigger nudge to avoid "shadow acne" — false self-shadowing
        // from ordinary depth-precision rounding.
        float bias = max(0.0025 * (1.0 - ndotl), 0.0006);

        float lit = 0.0;
        for (int x = -1; x <= 1; x++)
        {
            for (int y = -1; y <= 1; y++)
            {
                float sampleDepth = texture(shadowMap, posLightSpace.xy + vec2(x, y) * shadowMapTexel).r;
                lit += (posLightSpace.z - bias > sampleDepth) ? 0.0 : 1.0;
            }
        }
        return lit / 9.0;
    }

    void main()
    {
        vec3 n = normalize(fragNormal);
        float ndotl = max(dot(n, -lightDir), 0.0);

        // No point sampling the shadow map for a face already turned away
        // from the light — it's in its own shadow regardless.
        float shadow = ndotl > 0.0 ? ShadowFactor(fragPosition, ndotl) : 1.0;

        vec3 lit = fragColor.rgb * (ambientColor + lightColor * ndotl * shadow);

        // Local glow sources (campfires) — additive, unshadowed (a full
        // shadow map per point light would be a lot of machinery for a
        // small cosmetic glow), falling off smoothly to zero at
        // POINT_LIGHT_RADIUS so a fire lights its immediate surroundings
        // without needing per-light attenuation tuning from the C# side.
        for (int i = 0; i < pointLightCount; i++)
        {
            vec3 toLight = pointLightPos[i] - fragPosition;
            float dist = length(toLight);
            float pndotl = max(dot(n, toLight / max(dist, 0.0001)), 0.0);
            float atten = clamp(1.0 - dist / POINT_LIGHT_RADIUS, 0.0, 1.0);
            lit += fragColor.rgb * pointLightColor[i] * pndotl * (atten * atten);
        }

        finalColor = vec4(lit, fragColor.a);
    }
    """;

  // Seconds for one full day/night cycle, midnight to midnight — long
  // enough to read as a proper day, short enough to actually watch happen.
  const float DayLengthSeconds = 120f;

  // Resolution of the shadow map's depth texture: the whole map is rendered
  // into this once per frame from the light's point of view. 2048 keeps
  // tree-trunk-width shadows crisp without being a heavy render target.
  const int ShadowMapSize = 2048;

  // A high, deliberately-out-of-the-way texture slot for the shadow map —
  // raylib's own material system claims the low slots (albedo/normal/.../
  // brdf are indices 0-10) for ordinary textured draws, so this sits well
  // clear of all of them. Rlgl.ActiveTextureSlot/EnableTexture/SetUniform
  // (rather than the higher-level SetShaderValueTexture, which doesn't
  // give control over which slot it picks) is what makes claiming a
  // specific slot possible.
  const int ShadowMapTextureSlot = 15;

  // Has to match FragmentSrc's MAX_POINT_LIGHTS exactly — extra lights
  // beyond this are silently dropped by SetPointLights.
  public const int MaxPointLights = 8;

  readonly Shader _shader;
  readonly int _lightDirLoc, _lightColorLoc, _ambientColorLoc, _lightVPLoc, _shadowMapLoc;
  readonly int _pointLightPosLoc, _pointLightColorLoc, _pointLightCountLoc;
  readonly Vector3[] _pointLightPosBuffer = new Vector3[MaxPointLights];
  readonly Vector3[] _pointLightColorBuffer = new Vector3[MaxPointLights];

  readonly uint _shadowFboId;
  readonly Texture2D _shadowDepthTexture;
  readonly RenderTexture2D _shadowTarget;

  // Normalised time of day: 0 = midnight, 0.25 = sunrise, 0.5 = noon,
  // 0.75 = sunset, wrapping back to 0. Starts mid-morning so the scene
  // isn't dark the instant the game opens.
  float _time = 0.35f;

  float _speedMultiplier = 0.1f;

  // Direction the light travels (sun/moon -> ground) — what the shader
  // needs. Recomputed every frame in Recompute().
  Vector3 _lightDir;
  bool _isNight;

  public Shader Shader => _shader;
  public Color SkyColor { get; private set; }

  // While true, Update() leaves time of day exactly where it is — the sun
  // (and its shadows) stop moving until this is cleared again.
  public bool Frozen { get; set; }

  // How many in-game "days" pass per real second, relative to the normal
  // DayLengthSeconds pace. 1 = normal speed, 0 = effectively frozen, 2 =
  // twice as fast. Clamped so a stray UI value can't send time backwards
  // or into an absurdly fast spin.
  public float SpeedMultiplier
  {
    get => _speedMultiplier;
    set => _speedMultiplier = Math.Clamp(value, 0f, 10f);
  }

  // Normalised time of day (0 = midnight ... 1 = next midnight). Settable
  // so a UI slider can jump straight to a moment — the cycle then keeps
  // running from wherever it was dropped, unless Frozen is also set.
  public float TimeOfDay
  {
    get => _time;
    set
    {
      _time = ((value % 1f) + 1f) % 1f;
      Recompute();
    }
  }

  // One point on the day/night gradient: how bright the sky/ground/light
  // itself look at some moment. Intensity separately scales how much the
  // direct light contributes, so "the light itself is warm-coloured but
  // faint" (dawn/dusk) can be expressed distinctly from "there's no direct
  // light at all, just ambient" (deep night).
  readonly record struct SkyFrame(Vector3 Sky, Vector3 Light, Vector3 Ambient, float Intensity);

  static readonly SkyFrame Midnight = new(
    Sky: new Vector3(0.02f, 0.03f, 0.09f),
    Light: new Vector3(0.45f, 0.55f, 0.85f),
    Ambient: new Vector3(0.05f, 0.06f, 0.11f),
    Intensity: 0f);

  static readonly SkyFrame Dawn = new(
    Sky: new Vector3(0.95f, 0.55f, 0.35f),
    Light: new Vector3(1.0f, 0.65f, 0.45f),
    Ambient: new Vector3(0.30f, 0.24f, 0.26f),
    Intensity: 0.75f);

  static readonly SkyFrame Noon = new(
    Sky: new Vector3(0.45f, 0.72f, 0.90f),
    Light: new Vector3(1.0f, 0.97f, 0.90f),
    Ambient: new Vector3(0.38f, 0.42f, 0.50f),
    Intensity: 1f);

  static readonly SkyFrame Dusk = new(
    Sky: new Vector3(0.85f, 0.40f, 0.30f),
    Light: new Vector3(1.0f, 0.55f, 0.35f),
    Ambient: new Vector3(0.28f, 0.20f, 0.24f),
    Intensity: 0.7f);

  // Evenly spaced at t = 0, 0.25, 0.5, 0.75 and wrapping, so the cycle
  // (dawn brightening, daylight, dusk fading, and the deep-night trough
  // between dusk and the next dawn) all fall out of one shared blend.
  static readonly SkyFrame[] Keyframes = { Midnight, Dawn, Noon, Dusk };

  public SunLight()
  {
    _shader = Raylib.LoadShaderFromMemory(VertexSrc, FragmentSrc(ShadowMapSize));
    _lightDirLoc = Raylib.GetShaderLocation(_shader, "lightDir");
    _lightColorLoc = Raylib.GetShaderLocation(_shader, "lightColor");
    _ambientColorLoc = Raylib.GetShaderLocation(_shader, "ambientColor");
    _lightVPLoc = Raylib.GetShaderLocation(_shader, "lightVP");
    _shadowMapLoc = Raylib.GetShaderLocation(_shader, "shadowMap");
    _pointLightPosLoc = Raylib.GetShaderLocation(_shader, "pointLightPos");
    _pointLightColorLoc = Raylib.GetShaderLocation(_shader, "pointLightColor");
    _pointLightCountLoc = Raylib.GetShaderLocation(_shader, "pointLightCount");

    (_shadowFboId, _shadowDepthTexture) = LoadShadowMap(ShadowMapSize);
    _shadowTarget = new RenderTexture2D
    {
      Id = _shadowFboId,
      Texture = new Texture2D { Width = ShadowMapSize, Height = ShadowMapSize },
      Depth = _shadowDepthTexture,
    };

    // Bound up front so the very first depth pass (before RenderShadowMap
    // has ever run once to set this "for real") still has a valid texture
    // on the shadowMap sampler rather than nothing at all.
    BindShadowMapSampler();

    Recompute();
  }

  // Binds the shadow map's depth texture to the shader's shadowMap
  // uniform on a fixed, explicit slot. See ShadowMapTextureSlot for why
  // this can't just be the more convenient Raylib.SetShaderValueTexture.
  unsafe void BindShadowMapSampler()
  {
    Rlgl.EnableShader(_shader.Id);
    Rlgl.ActiveTextureSlot(ShadowMapTextureSlot);
    Rlgl.EnableTexture(_shadowDepthTexture.Id);
    int slot = ShadowMapTextureSlot;
    Rlgl.SetUniform(_shadowMapLoc, &slot, (int)ShaderUniformDataType.Int, 1);
  }

  // A framebuffer with only a depth attachment (no colour): all that's
  // needed to record, per pixel, how far the nearest thing the light can
  // see actually is. LoadTextureDepth(..., useRenderBuffer: false) is the
  // part that matters — a render*buffer* depth attachment (the raylib
  // default for an ordinary RenderTexture2D) can't be sampled back as a
  // texture in a shader; this variant can.
  static (uint FboId, Texture2D Depth) LoadShadowMap(int size)
  {
    uint fbo = Rlgl.LoadFramebuffer();
    uint depthId = Rlgl.LoadTextureDepth(size, size, useRenderBuffer: false);

    Rlgl.EnableFramebuffer(fbo);
    Rlgl.FramebufferAttach(fbo, depthId, FramebufferAttachType.Depth, FramebufferAttachTextureType.Texture2D, 0);
    Rlgl.DisableFramebuffer();

    var depth = new Texture2D { Id = depthId, Width = size, Height = size, Mipmaps = 1, Format = PixelFormat.UncompressedR32 };
    return (fbo, depth);
  }

  public void Update(float dt)
  {
    if (Frozen) return;
    _time = (_time + dt * _speedMultiplier / DayLengthSeconds) % 1f;
    Recompute();
  }

  void Recompute()
  {
    // elevation: -1 at midnight (straight down/underground), 0 at
    // sunrise/sunset (on the horizon), 1 at noon (straight up). azimuth
    // sweeps a full turn across the day, so shadows visibly swing around
    // over a cycle rather than just lengthening and shrinking in place.
    float phase = _time * MathF.Tau;
    float elevation = -MathF.Cos(phase);
    float horizontalRadius = MathF.Sqrt(MathF.Max(0f, 1f - elevation * elevation));
    Vector3 towardLight = new(MathF.Cos(phase) * horizontalRadius, elevation, MathF.Sin(phase) * horizontalRadius);

    _lightDir = -towardLight;
    _isNight = elevation < 0f;

    var frame = Sample(_time);
    SkyColor = ToColor(frame.Sky);

    Raylib.SetShaderValue(_shader, _lightDirLoc, _lightDir, ShaderUniformDataType.Vec3);
    Raylib.SetShaderValue(_shader, _lightColorLoc, frame.Light * frame.Intensity, ShaderUniformDataType.Vec3);
    Raylib.SetShaderValue(_shader, _ambientColorLoc, frame.Ambient, ShaderUniformDataType.Vec3);
  }

  static SkyFrame Sample(float t)
  {
    float scaled = t * Keyframes.Length;
    int i0 = (int)MathF.Floor(scaled) % Keyframes.Length;
    int i1 = (i0 + 1) % Keyframes.Length;
    float frac = scaled - MathF.Floor(scaled);
    float blend = frac * frac * (3f - 2f * frac); // smoothstep: gentler than a linear cross-fade

    var a = Keyframes[i0];
    var b = Keyframes[i1];
    return new SkyFrame(
      Vector3.Lerp(a.Sky, b.Sky, blend),
      Vector3.Lerp(a.Light, b.Light, blend),
      Vector3.Lerp(a.Ambient, b.Ambient, blend),
      a.Intensity + (b.Intensity - a.Intensity) * blend);
  }

  static Color ToColor(Vector3 rgb01) => new(
    (byte)Math.Clamp(rgb01.X * 255f, 0, 255),
    (byte)Math.Clamp(rgb01.Y * 255f, 0, 255),
    (byte)Math.Clamp(rgb01.Z * 255f, 0, 255),
    (byte)255);

  // Renders the map's terrain, trees, bushes, and actors into the shadow
  // map from the light's point of view, then wires the result (the
  // light's combined view-projection matrix, plus the depth texture
  // itself) into the main shader so the very next lit pass already sees
  // today's shadows. Has to run once per frame, not just when the light
  // moves a lot: the light drifts continuously, so a cached shadow map
  // would start going stale immediately.
  public void RenderShadowMap(TileMap map, IEnumerable<Actor> actors)
  {
    float mapHeight = TileMap.MaxHeight * TileMap.TileSize;
    Vector3 boundsCenter = new(map.WidthPx / 2f, mapHeight / 2f, map.DepthPx / 2f);
    float radius = 0.5f * new Vector3(map.WidthPx, mapHeight, map.DepthPx).Length();

    // Up has to not be parallel with the view direction — which it would
    // be for an overhead (near-noon) or straight-up (near-midnight) light,
    // where the "natural" world-up would make for a degenerate view matrix.
    Vector3 up = MathF.Abs(Vector3.Dot(_lightDir, Vector3.UnitY)) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;

    var lightCamera = new Camera3D
    {
      Position = boundsCenter - _lightDir * radius * 2f,
      Target = boundsCenter,
      Up = up,
      FovY = radius * 2.05f, // orthographic view height in world units — a hair over the bounding sphere so nothing right at the edge clips
      Projection = CameraProjection.Orthographic,
    };

    // The default near/far clip range is nowhere near big enough for a
    // scene-spanning orthographic box on a larger map, so it's widened for
    // this pass and restored immediately after for the normal game camera.
    double prevNear = Rlgl.GetCullDistanceNear();
    double prevFar = Rlgl.GetCullDistanceFar();
    Rlgl.SetClipPlanes(0.1, radius * 4.0);

    Raylib.BeginTextureMode(_shadowTarget);
    Raylib.ClearBackground(Color.White);
    Raylib.BeginMode3D(lightCamera);
    // Rlgl.GetMatrixModelview/Projection() come back in raylib's own
    // native layout — combining them with System.Numerics.Matrix4x4's own
    // `*` operator silently assumes a different (row-vector) convention
    // and produces a wrong, effectively-degenerate result. Raymath.
    // MatrixMultiply is a real binding of raylib's own C MatrixMultiply,
    // so it combines them exactly the way raylib combines its own
    // automatic mvp internally.
    Matrix4x4 lightView = Rlgl.GetMatrixModelview();
    Matrix4x4 lightProj = Rlgl.GetMatrixProjection();
    Matrix4x4 lightViewProj = Raymath.MatrixMultiply(lightView, lightProj);
    Raylib.BeginShaderMode(_shader);
    map.DrawSolid();
    map.DrawTrees();
    map.DrawBushes();
    map.DrawCampfiresLit();
    foreach (var actor in actors) actor.DrawShadowCaster();
    Raylib.EndShaderMode();
    Raylib.EndMode3D();
    Raylib.EndTextureMode();

    Rlgl.SetClipPlanes(prevNear, prevFar);

    Raylib.SetShaderValueMatrix(_shader, _lightVPLoc, lightViewProj);
    BindShadowMapSampler();
  }

  // Uploads up to MaxPointLights local glow sources (currently just
  // campfires — see TileMap.CampfireLights) on top of the one directional
  // sun/moon light above. Extra lights past the cap are silently dropped.
  // Call once per frame before the lit pass; harmless to call with zero
  // lights (pointLightCount just stays/becomes 0 and the shader's loop
  // over it doesn't run).
  public void SetPointLights(IReadOnlyList<Vector3> positions, IReadOnlyList<Vector3> colors)
  {
    int count = Math.Min(positions.Count, MaxPointLights);
    for (int i = 0; i < count; i++)
    {
      _pointLightPosBuffer[i] = positions[i];
      _pointLightColorBuffer[i] = colors[i];
    }

    Raylib.SetShaderValueV(_shader, _pointLightPosLoc, _pointLightPosBuffer, ShaderUniformDataType.Vec3, count);
    Raylib.SetShaderValueV(_shader, _pointLightColorLoc, _pointLightColorBuffer, ShaderUniformDataType.Vec3, count);
    Raylib.SetShaderValue(_shader, _pointLightCountLoc, count, ShaderUniformDataType.Int);
  }

  // Wrap the lit draw calls (terrain fill, water, actors) between these.
  public void BeginLit() => Raylib.BeginShaderMode(_shader);
  public void EndLit() => Raylib.EndShaderMode();

  // The sun by day, a pale moon (roughly opposite the sun, since it's not
  // worth modelling real moon phases here) by night. Drawn unlit — it IS
  // the light — so call this outside BeginLit/EndLit.
  public void DrawCelestialBodies(Vector3 sceneCenter, float distance = 1100f)
  {
    Vector3 towardLight = -_lightDir;

    if (!_isNight)
    {
      Vector3 sunPos = sceneCenter + towardLight * distance;
      Raylib.DrawSphere(sunPos, 60f, new Color(255, 244, 210, 255));
      Raylib.DrawSphere(sunPos, 95f, new Color(255, 244, 210, 50));
    }
    else
    {
      Vector3 moonPos = sceneCenter - towardLight * distance;
      Raylib.DrawSphere(moonPos, 45f, new Color(210, 215, 230, 255));
      Raylib.DrawSphere(moonPos, 70f, new Color(210, 215, 230, 40));
    }
  }

  public void Unload()
  {
    Rlgl.UnloadFramebuffer(_shadowFboId);
    Raylib.UnloadShader(_shader);
  }
}
