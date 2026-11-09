using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

/*
    Research probe for runtime lighting (dev only): what the game's renderer does with the sun, ambient, fog and
    extra lights. `logic-light raw ...`.
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal static class LightProbe
    {
        public static string Run(string[] a)
        {
            var clm = ColorAndLightManager.Instance;
            var sun = clm != null ? clm.Sunlight : null;
            string what = a.Length > 0 ? a[0] : "info";
            switch (what)
            {
                case "info":
                {
                    var sb = new StringBuilder();
                    sb.Append($"sun={(sun != null ? $"{sun.color} i={sun.intensity} shadows={sun.shadows} rot={sun.transform.eulerAngles}" : "none")}\n");
                    sb.Append($"ambientMode={RenderSettings.ambientMode} ambient={RenderSettings.ambientLight} sky={RenderSettings.ambientSkyColor} intensity={RenderSettings.ambientIntensity}\n");
                    sb.Append($"fog={RenderSettings.fog} mode={RenderSettings.fogMode} color={RenderSettings.fogColor} density={RenderSettings.fogDensity} start={RenderSettings.fogStartDistance} end={RenderSettings.fogEndDistance}\n");
                    var rp = GraphicsSettings.currentRenderPipeline;
                    sb.Append($"pipeline={rp?.GetType().Name} {rp?.name}\n");
                    if (rp != null)
                        foreach (var p in rp.GetType().GetProperties().Where(p => p.Name.ToLower().Contains("light") || p.Name.ToLower().Contains("shadow")))
                        {
                            try { sb.Append($"  {p.Name}={p.GetValue(rp)}\n"); } catch { }
                        }
                    foreach (var l in Object.FindObjectsOfType<Light>(true))
                        sb.Append($"light {l.name} type={l.type} on={l.enabled && l.gameObject.activeInHierarchy} c={l.color} i={l.intensity} r={l.range} mode={l.renderMode} at={l.transform.position}\n");
                    var cam = Camera.main;
                    if (cam != null) sb.Append($"cam clear={cam.clearFlags} bg={cam.backgroundColor} ortho={cam.orthographic}\n");
                    var mat = Object.FindObjectsOfType<Renderer>().Select(r => r.sharedMaterial).FirstOrDefault(m => m != null && m.name.StartsWith("Ground"));
                    if (mat != null) sb.Append($"ground mat {mat.name} shader={mat.shader.name} kw=[{string.Join(",", mat.shaderKeywords)}] lc={(mat.HasProperty("_LightContribution") ? mat.GetFloat("_LightContribution") : -1)}\n");
                    return sb.ToString();
                }
                case "sun":
                    if (sun == null) return "no sun";
                    if (a.Length > 1 && ColorUtility.TryParseHtmlString(a[1], out var c)) sun.color = c;
                    if (a.Length > 1 && a[1] == "night") sun.color = clm.CurrentColorScheme.nightLightColor;
                    if (a.Length > 2) sun.intensity = float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture);
                    clm.AdjustAllOutlineColorsBasedOnLight(clm.CurrentColorScheme.globalShadowColor, sun.color);
                    return $"sun {sun.color} {sun.intensity}";
                case "ambient":
                    if (a.Length > 1 && ColorUtility.TryParseHtmlString(a[1], out var amb))
                    {
                        RenderSettings.ambientMode = AmbientMode.Flat;
                        RenderSettings.ambientLight = amb;
                    }
                    return $"ambient {RenderSettings.ambientLight}";
                case "fog":
                    if (a.Length > 1 && a[1] == "off") { RenderSettings.fog = false; return "fog off"; }
                    RenderSettings.fog = true;
                    if (a.Length > 1 && ColorUtility.TryParseHtmlString(a[1], out var fc)) RenderSettings.fogColor = fc;
                    RenderSettings.fogMode = FogMode.Linear;
                    if (a.Length > 3)
                    {
                        RenderSettings.fogStartDistance = float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture);
                        RenderSettings.fogEndDistance = float.Parse(a[3], System.Globalization.CultureInfo.InvariantCulture);
                    }
                    return $"fog {RenderSettings.fogColor} {RenderSettings.fogStartDistance}-{RenderSettings.fogEndDistance}";
                case "point":
                {
                    var go = new GameObject("PalaceBuilder probe light");
                    float x = float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture), z = float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture);
                    go.transform.position = new Vector3(x, a.Length > 7 ? float.Parse(a[7], System.Globalization.CultureInfo.InvariantCulture) : 4f, z);
                    var l = go.AddComponent<Light>();
                    l.type = a.Length > 6 ? LightType.Spot : LightType.Point;
                    l.range = float.Parse(a[3], System.Globalization.CultureInfo.InvariantCulture);
                    l.intensity = float.Parse(a[4], System.Globalization.CultureInfo.InvariantCulture);
                    if (ColorUtility.TryParseHtmlString(a[5], out var pc)) l.color = pc;
                    if (l.type == LightType.Spot) { l.spotAngle = float.Parse(a[6], System.Globalization.CultureInfo.InvariantCulture); go.transform.rotation = Quaternion.Euler(60f, 0f, 0f); }
                    l.renderMode = LightRenderMode.ForcePixel;
                    return $"light at {go.transform.position}";
                }
                case "maxlights":
                {
                    var rp = GraphicsSettings.currentRenderPipeline;
                    var prop = rp?.GetType().GetProperty("maxAdditionalLightsCount");
                    if (prop == null) return "no property";
                    if (prop.CanWrite) prop.SetValue(rp, int.Parse(a[1]));
                    return $"maxAdditionalLightsCount={prop.GetValue(rp)} writable={prop.CanWrite}";
                }
                case "clear":
                    foreach (var l in Object.FindObjectsOfType<Light>().Where(l => l.name == "PalaceBuilder probe light")) Object.Destroy(l.gameObject);
                    return "cleared";
                case "scheme":
                {
                    // Multiply every scheme colour (lit, mid, shadow) by k: does the toon look darken overall?
                    if (clm == null) return "no manager";
                    float k = float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture);
                    var cur = clm.CurrentColorScheme;
                    var copy = Object.Instantiate(cur);
                    foreach (var f in typeof(Colorscheme).GetFields().Where(f => f.FieldType == typeof(Color) && !f.IsInitOnly))
                    {
                        var v = (Color)f.GetValue(cur);
                        f.SetValue(copy, new Color(v.r * k, v.g * k, v.b * k, v.a));
                    }
                    clm.ApplyColorScheme(copy);
                    return "scheme x" + k;
                }
                case "restore":
                    clm?.ApplyColorScheme(clm.CurrentColorScheme);
                    if (sun != null) { sun.intensity = 1f; }
                    RenderSettings.fog = false;
                    return "restored";
            }
            return "raw info | sun #hex [i] | ambient #hex | fog #hex start end | fog off | point x z range i #hex [spot] [y] | clear | scheme k | restore";
        }
    }
}
