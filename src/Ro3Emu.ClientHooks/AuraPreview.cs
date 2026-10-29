using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace Ro3Emu.ClientHooks;

/// <summary>Original animated flame aura. Owns its particles and texture; no gameplay buff or wardrobe slot.</summary>
internal sealed class AuraPreview : MonoBehaviour
{
    private int _owner;
    private Renderer _target;
    private GameObject _effect;
    private ParticleSystem _flames;
    private Material _material;
    private Texture2D _texture;

    internal static void Ensure(RD_Renderer renderer, int owner)
    {
        var current = renderer.GetComponent<AuraPreview>();
        if (current != null && current._target == renderer.GetRenderer() && current._owner == owner) return;
        Show(renderer, true);
        var components = renderer.GetComponents<AuraPreview>();
        components[components.Length - 1]._owner = owner;
    }

    internal static void HideOwned(RD_Renderer renderer, int owner)
    {
        foreach (var current in renderer.GetComponents<AuraPreview>())
            if (current._owner == owner) { current.Clear(); Destroy(current); }
    }

    internal static void Show(RD_Renderer renderer, bool enabled)
    {
        foreach (var existing in renderer.GetComponents<AuraPreview>()) { existing.Clear(); Destroy(existing); }
        if (!enabled) return;
        var shader = Shader.Find("Sprites/Default");
        if (shader == null) throw new InvalidOperationException("Flame shader unavailable.");
        var effect = renderer.gameObject.AddComponent<AuraPreview>();
        try { effect.Create(renderer.GetRenderer(), shader); }
        catch { effect.Clear(); Destroy(effect); throw; }
    }

    private void Create(Renderer target, Shader shader)
    {
        _target = target;
        _texture = FlameTexture();
        _material = new Material(shader) { name = "Sol GM flames", mainTexture = _texture };
        _effect = new GameObject("SolGmFlames") { layer = gameObject.layer };
        _flames = _effect.AddComponent<ParticleSystem>();
        _flames.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        ConfigureMotion();
        ConfigureRendering();
        Follow();
        _flames.Play();
    }

    private void ConfigureMotion()
    {
        var main = _flames.main;
        main.loop = true;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.scalingMode = ParticleSystemScalingMode.Hierarchy;
        main.startLifetime = new ParticleSystem.MinMaxCurve(.8f, 1.2f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(1.4f, 2f);
        main.startSize3D = true;
        main.startSizeX = new ParticleSystem.MinMaxCurve(.5f, .85f);
        main.startSizeY = new ParticleSystem.MinMaxCurve(1.5f, 2.2f);
        main.startSizeZ = 1;
        main.startRotation = new ParticleSystem.MinMaxCurve(-.12f, .12f);
        main.maxParticles = 64;
    }

    private void ConfigureRendering()
    {
        var emission = _flames.emission;
        emission.rateOverTime = 48;
        var shape = _flames.shape;
        shape.shapeType = ParticleSystemShapeType.Circle;
        shape.radius = .46f;
        shape.radiusThickness = .2f;
        shape.rotation = new Vector3(-90, 0, 0);
        var size = _flames.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1, new AnimationCurve(new Keyframe(0, .6f), new Keyframe(.2f, 1), new Keyframe(1, .15f)));
        var colour = _flames.colorOverLifetime;
        colour.enabled = true;
        colour.color = FlameColours();
        var visual = _flames.GetComponent<ParticleSystemRenderer>();
        visual.sharedMaterial = _material;
        visual.renderMode = ParticleSystemRenderMode.Billboard;
        visual.sortMode = ParticleSystemSortMode.Distance;
        visual.shadowCastingMode = ShadowCastingMode.Off;
        visual.receiveShadows = false;
    }

    private static Gradient FlameGradient()
    {
        var gradient = new Gradient();
        gradient.SetKeys(new[] {
            new GradientColorKey(Color.white, 0),
            new GradientColorKey(new Color(.8f, .95f, 1), .3f),
            new GradientColorKey(new Color(.25f, .75f, 1), .7f),
            new GradientColorKey(new Color(.03f, .2f, .7f), 1)
        }, new[] {
            new GradientAlphaKey(0, 0), new GradientAlphaKey(.72f, .12f),
            new GradientAlphaKey(.6f, .55f), new GradientAlphaKey(0, 1)
        });
        return gradient;
    }

    private static ParticleSystem.MinMaxGradient FlameColours()
    {
        // The shipped Unity module strips the Gradient constructor/setters but retains these serialized fields.
        object value = new ParticleSystem.MinMaxGradient(Color.white);
        var type = typeof(ParticleSystem.MinMaxGradient);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var mode = type.GetField("m_Mode", flags);
        var gradient = type.GetField("m_GradientMax", flags);
        if (mode == null || gradient == null) throw new InvalidOperationException("Particle colour gradient unavailable.");
        mode.SetValue(value, ParticleSystemGradientMode.Gradient);
        gradient.SetValue(value, FlameGradient());
        return (ParticleSystem.MinMaxGradient)value;
    }

    // Authored teardrop flame silhouette with an irregular edge, not a borrowed texture or a solid body tint.
    private static Texture2D FlameTexture()
    {
        const int size = 64;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "Sol flame tongue", wrapMode = TextureWrapMode.Clamp };
        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float v = (y + .5f) / size, u = (x + .5f) / size;
                float centre = .5f + .10f * Mathf.Sin(v * 9) * v;
                float width = .43f * Mathf.Pow(1 - v, .75f) * (.85f + .15f * Mathf.Sin(v * 23));
                float edge = Mathf.Abs(u - centre) / width;
                float alpha = Mathf.Pow(Mathf.Clamp01(1 - edge), .65f) * Mathf.Clamp01(v * 12);
                float core = Mathf.Clamp01(1 - edge * 1.7f) * Mathf.Clamp01((1 - v) * 1.6f);
                pixels[y * size + x] = new Color(.08f + .92f * core, .6f + .4f * core, 1, alpha);
            }
        texture.SetPixels(pixels);
        texture.Apply(false, true);
        return texture;
    }

    private void LateUpdate()
    {
        if (_effect == null || _target == null) return;
        bool visible = _target.enabled && _target.gameObject.activeInHierarchy && _target.isVisible;
        if (_effect.activeSelf != visible)
        {
            _effect.SetActive(visible);
            if (visible) _flames.Play();
        }
        if (visible) Follow();
    }

    private void Follow()
    {
        var bounds = _target.bounds;
        _effect.transform.position = new Vector3(bounds.center.x, bounds.min.y + .04f, bounds.center.z);
        _effect.transform.localScale = Vector3.one * Mathf.Clamp(bounds.size.y / 1.6f, .65f, 1.8f);
    }

    private void OnDisable() => Clear();
    private void OnDestroy() => Clear();
    private void Clear()
    {
        _target = null;
        if (_effect != null) { _effect.SetActive(false); Destroy(_effect); }
        _effect = null; _flames = null;
        if (_material != null) Destroy(_material);
        _material = null;
        if (_texture != null) Destroy(_texture);
        _texture = null;
    }
}
