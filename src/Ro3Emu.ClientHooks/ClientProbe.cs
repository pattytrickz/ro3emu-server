using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Ro3Emu.ClientHooks;

/// <summary>Opt-in local PTR inspection. RO3EMU_CLIENT_PROBE names a directory containing request.txt.
/// Commands exercise the real UI and renderer; they confer no server permissions. Credentials are never logged.</summary>
internal static class ClientProbe
{
    private static readonly string DirectoryName = Environment.GetEnvironmentVariable("RO3EMU_CLIENT_PROBE");
    private static int _frame;

    internal static void Tick()
    {
        if (string.IsNullOrEmpty(DirectoryName) || ++_frame % 15 != 0) return;
        Application.runInBackground = true;
        string request = System.IO.Path.Combine(DirectoryName, "request.txt");
        if (!File.Exists(request)) return;
        try
        {
            string command = File.ReadAllText(request).TrimEnd('\r', '\n');
            File.Delete(request);
            Run(command.Split('\t'));
        }
        catch (Exception e) { Log("ERROR " + e.GetType().Name + ": " + e.Message); }
    }

    private static void Run(string[] args)
    {
        switch (args[0])
        {
            case "ui": DumpUi(); break;
            case "find":
                foreach (var transform in Resources.FindObjectsOfTypeAll<Transform>().Where(t => t.gameObject.activeInHierarchy
                    && t.name.Contains(args[1], StringComparison.OrdinalIgnoreCase)).Take(100)) Log(PathOf(transform));
                break;
            case "models": DumpModels(); break;
            case "capture":
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(DirectoryName, SafeName(args[1]) + ".png"));
                Log("Capture requested");
                break;
            case "fill":
                var field = Resources.FindObjectsOfTypeAll<TMP_InputField>()
                    .Single(t => t.gameObject.activeInHierarchy && PathOf(t.transform) == args[1]);
                field.text = args[2];
                Log("Input field set");
                break;
            case "click": Click(args[1], args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : -1); break;
            case "tap": Tap(Number(args[1]), Number(args[2])); break;
            case "use-bag-item": CharacterAppearance.ProbeUseBagItem(long.Parse(args[1], CultureInfo.InvariantCulture)); Log("Bag Use action invoked"); break;
            case "textures": DumpTextures(Renderer(args[1])); break;
            case "dye":
                DyePreview.Apply(Renderer(args[1]), new Color(Number(args[2]), Number(args[3]), Number(args[4]), 1));
                Log("Dye preview applied");
                break;
            case "reset": DyePreview.Reset(Renderer(args[1])); Log("Dye preview reset"); break;
            case "aura": AuraPreview.Show(Renderer(args[1]), args[2] == "on"); Log("Aura preview updated"); break;
            default: Log("Unknown probe command"); break;
        }
    }

    private static void DumpUi()
    {
        var output = new StringBuilder("UI\n");
        foreach (var field in Resources.FindObjectsOfTypeAll<TMP_InputField>().Where(f => f.gameObject.activeInHierarchy))
            output.Append("INPUT\t").AppendLine(PathOf(field.transform));
        foreach (var button in Resources.FindObjectsOfTypeAll<Button>().Where(b => b.gameObject.activeInHierarchy))
            output.Append("BUTTON\t").Append(PathOf(button.transform)).Append('\t').AppendLine(button.interactable.ToString());
        foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>().Where(t => t.gameObject.activeInHierarchy
            && t.GetComponentInParent<TMP_InputField>() == null).Take(160))
            output.Append("TEXT\t").Append(PathOf(text.transform)).Append('\t').AppendLine(text.text);
        Log(output.ToString());
    }

    private static void Click(string path, int index)
    {
        var targets = Resources.FindObjectsOfTypeAll<Transform>().Where(t => t.gameObject.activeInHierarchy && PathOf(t) == path).ToArray();
        var target = index < 0 ? targets.Single() : targets[index];
        var pointer = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left };
        ExecuteEvents.Execute(target.gameObject, pointer, ExecuteEvents.pointerClickHandler);
        Log("UI click dispatched");
    }

    private static void Tap(float x, float y)
    {
        var pointer = new PointerEventData(EventSystem.current)
        {
            button = PointerEventData.InputButton.Left, position = new Vector2(x, Screen.height - y)
        };
        var hits = new List<RaycastResult>();
        EventSystem.current.RaycastAll(pointer, hits);
        if (hits.Count == 0) throw new ArgumentException("No UI at this screen position.");
        var target = hits[0].gameObject;
        ExecuteEvents.ExecuteHierarchy(target, pointer, ExecuteEvents.pointerEnterHandler);
        ExecuteEvents.ExecuteHierarchy(target, pointer, ExecuteEvents.pointerDownHandler);
        ExecuteEvents.ExecuteHierarchy(target, pointer, ExecuteEvents.pointerUpHandler);
        ExecuteEvents.ExecuteHierarchy(target, pointer, ExecuteEvents.pointerClickHandler);
        Log("UI tap dispatched: " + PathOf(target.transform));
    }

    private static void DumpModels()
    {
        var output = new StringBuilder("MODELS\n");
        foreach (var renderer in Resources.FindObjectsOfTypeAll<RD_Renderer>().Where(r => r.gameObject.activeInHierarchy && r.gameObject.scene.IsValid()))
        {
            output.Append(renderer.GetInstanceID()).Append('\t').Append(PathOf(renderer.transform))
                .Append('\t').AppendLine(renderer.GetRendererData() != null ? renderer.GetRendererData().partType.ToString() : "no data");
            var materials = renderer.GetMaterials();
            if (materials == null) continue;
            foreach (var material in materials.Where(m => m != null))
                output.Append("  ").Append(material.name).Append(" shader=").Append(material.shader.name)
                    .Append(" textures=").AppendLine(string.Join(",", material.GetTexturePropertyNames()));
        }
        Log(output.ToString());
    }

    private static void DumpTextures(RD_Renderer renderer)
    {
        int index = 0;
        foreach (var material in renderer.GetMaterials())
        {
            if (material == null) continue;
            foreach (string property in material.GetTexturePropertyNames())
            {
                var texture = material.GetTexture(property);
                if (texture == null) continue;
                string file = renderer.GetInstanceID() + "_" + index + "_" + SafeName(property) + ".png";
                Diagnostics.SavePng(texture, System.IO.Path.Combine(DirectoryName, file));
                Log("Texture " + file + " " + texture.name);
            }
            index++;
        }
    }

    private static RD_Renderer Renderer(string id) => Resources.FindObjectsOfTypeAll<RD_Renderer>()
        .Single(r => r.GetInstanceID() == int.Parse(id, CultureInfo.InvariantCulture));
    private static float Number(string value) => float.Parse(value, CultureInfo.InvariantCulture);
    private static string SafeName(string value) => string.Concat(value.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-'));
    private static string PathOf(Transform value) => value.parent != null ? PathOf(value.parent) + "/" + value.name : value.name;
    private static void Log(string line) => File.AppendAllText(System.IO.Path.Combine(DirectoryName, "probe.log"),
        DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + line + Environment.NewLine);
}
