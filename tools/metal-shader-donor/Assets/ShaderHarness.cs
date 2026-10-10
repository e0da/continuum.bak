using System;
using System.Collections;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

public sealed class ShaderHarness : MonoBehaviour
{
    AssetBundle bundle;
    Shader donor;
    string output;
    int frame;
    bool visualMatch;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void StartHarness()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONTINUUM_AERO_WORKBENCH_OUTPUT"))) return;
        new GameObject("Continuum shader harness").AddComponent<ShaderHarness>();
    }

    void Start()
    {
        try { Configure(); }
        catch (Exception error)
        {
            Debug.LogException(error);
            if (!string.IsNullOrWhiteSpace(output))
            {
                try { File.WriteAllText(output, "status=setup-failed\nerror=" + error.GetType().Name + "\n"); }
                catch (Exception writeError) { Debug.LogException(writeError); }
            }
            enabled = false;
            Application.Quit(1);
        }
    }

    void Configure()
    {
        output = Environment.GetEnvironmentVariable("CONTINUUM_HARNESS_REPORT");
        var bundlePath = Environment.GetEnvironmentVariable("CONTINUUM_SHADER_BUNDLE");
        if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(bundlePath))
            throw new Exception("Set CONTINUUM_HARNESS_REPORT and CONTINUUM_SHADER_BUNDLE");

        bundle = AssetBundle.LoadFromFile(bundlePath);
        if (bundle == null) throw new Exception("Could not load shader bundle");
        foreach (var shader in bundle.LoadAllAssets<Shader>())
            if (shader.name == "UI/Default") donor = shader;
        if (donor == null) throw new Exception("Bundle lacks UI/Default");

        var camera = new GameObject("Camera").AddComponent<Camera>();
        camera.transform.position = new Vector3(0, 0, 10);
        camera.orthographic = true;
        camera.orthographicSize = 2;
        camera.backgroundColor = new Color(0.08f, 0.1f, 0.18f);
        camera.clearFlags = CameraClearFlags.SolidColor;

        var pixels = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        pixels.SetPixels(new[] { Color.red, Color.green, Color.blue, Color.yellow });
        pixels.Apply();
        var root = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
        root.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
        MakeImage(root.transform, "Unity reference", -175, null, pixels);
        MakeImage(root.transform, "Donor UI/Default", 175, donor, pixels);
    }

    static void MakeImage(Transform parent, string name, float x, Shader shader, Texture texture)
    {
        var node = new GameObject(name, typeof(RectTransform), typeof(RawImage));
        node.transform.SetParent(parent, false);
        var rect = node.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(300, 300);
        rect.anchoredPosition = new Vector2(x, 0);
        var image = node.GetComponent<RawImage>();
        image.texture = texture;
        if (shader != null) image.material = new Material(shader);
    }

    void Update()
    {
        frame++;
        if (frame == 60) StartCoroutine(Capture());
        if (frame != 90) return;
        long screenshotBytes = File.Exists(output + ".png") ? new FileInfo(output + ".png").Length : 0;
        File.WriteAllText(output,
            "unity=" + Application.unityVersion + "\n" +
            "renderer=" + SystemInfo.graphicsDeviceType + "\n" +
            "resolution=" + Screen.width.ToString(CultureInfo.InvariantCulture) + "x" + Screen.height.ToString(CultureInfo.InvariantCulture) + "\n" +
            "donor=" + donor.name + "\n" +
            "donor_supported=" + donor.isSupported + "\n" +
            "visual_match=" + visualMatch + "\n" +
            "screenshot_bytes=" + screenshotBytes + "\n");
        Application.Quit(visualMatch && donor.isSupported && screenshotBytes > 0 ? 0 : 1);
    }

    IEnumerator Capture()
    {
        yield return new WaitForEndOfFrame();
        var image = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
        image.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
        image.Apply();
        var background = new Color(0.08f, 0.1f, 0.18f);
        int[] offsets = { -90, 90 };
        float difference = 0, contrast = 0, spread = 0;
        Color first = Color.clear;
        bool firstSample = true;
        foreach (int y in offsets)
        foreach (int x in offsets)
        {
            var reference = image.GetPixel(Screen.width / 2 - 175 + x, Screen.height / 2 + y);
            var actual = image.GetPixel(Screen.width / 2 + 175 + x, Screen.height / 2 + y);
            difference = Mathf.Max(difference, ChannelDifference(reference, actual));
            contrast = Mathf.Max(contrast, ChannelDifference(reference, background));
            if (firstSample) { first = reference; firstSample = false; }
            else spread = Mathf.Max(spread, ChannelDifference(reference, first));
        }
        visualMatch = difference < 0.05f && contrast > 0.1f && spread > 0.2f;
        Destroy(image);
        ScreenCapture.CaptureScreenshot(output + ".png");
    }

    static float ChannelDifference(Color a, Color b)
    {
        return Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b));
    }

    void OnDestroy() { if (bundle != null) bundle.Unload(false); }
}
