using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Webkit;
using Java.Interop;

namespace LectorBiblioteca;

[Activity(
    Label = "Lector",
    MainLauncher = true,
    Exported = true,
    Theme = "@android:style/Theme.Material.Light.NoActionBar",
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.SmallestScreenSize |
                           ConfigChanges.ScreenLayout | ConfigChanges.KeyboardHidden | ConfigChanges.UiMode)]
public class MainActivity : Activity
{
    const string Host = "appassets.androidplatform.net";
    const int FileRequest = 4711;

    WebView? web;
    FrameLayout? root;
    IValueCallback? pendingChooser;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Android 15+ dibuja la app debajo de las barras del sistema: este contenedor las respeta.
        root = new FrameLayout(this);
        root.SetBackgroundColor(Color.ParseColor("#F3F1EC"));
        web = new WebView(this);
        root.AddView(web, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        SetContentView(root);
        root.SetOnApplyWindowInsetsListener(new InsetsListener());

        var s = web.Settings;
        s.JavaScriptEnabled = true;
        s.DomStorageEnabled = true;          // IndexedDB y localStorage: aquí quedan guardados los PDF y tus subrayados
        s.AllowFileAccess = false;
        s.AllowContentAccess = true;
        s.MediaPlaybackRequiresUserGesture = true;
        s.SetSupportZoom(false);
        s.BuiltInZoomControls = false;
        s.TextZoom = 100;

        web.SetWebViewClient(new AssetClient(this));
        web.SetWebChromeClient(new ChromeClient(this));
        web.AddJavascriptInterface(new Bridge(this), "Android");

        web.LoadUrl($"https://{Host}/assets/index.html");
    }

    // Botón atrás: primero cierra paneles y el libro, y solo después sale de la app.
    public override void OnBackPressed()
    {
        if (web == null) { base.OnBackPressed(); return; }
        web.EvaluateJavascript("(function(){return window.__back?window.__back():false})()", new BackResult(this));
    }

    protected override void OnPause()
    {
        web?.EvaluateJavascript("window.__save&&window.__save()", null);
        base.OnPause();
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != FileRequest || pendingChooser == null) return;

        Java.Lang.Object? value = null;
        if (resultCode == Result.Ok && data != null)
        {
            // Varios PDF: se leen uno a uno desde ClipData (un solo PDF viene en data.Data).
            var list = new List<Android.Net.Uri>();
            var clip = data.ClipData;
            if (clip != null)
            {
                for (int i = 0; i < clip.ItemCount; i++)
                {
                    var u = clip.GetItemAt(i)?.Uri;
                    if (u != null) list.Add(u);
                }
            }
            else if (data.Data != null) list.Add(data.Data);
            if (list.Count > 0)
                value = Java.Lang.Object.FromArray(list.ToArray());
        }
        pendingChooser.OnReceiveValue(value);
        pendingChooser = null;
    }

    // ---- sirve los archivos de Assets/www como si fueran una página segura (https) ----
    class AssetClient : WebViewClient
    {
        readonly MainActivity activity;
        public AssetClient(MainActivity a) { activity = a; }

        public override WebResourceResponse? ShouldInterceptRequest(WebView? view, IWebResourceRequest? request)
        {
            var url = request?.Url;
            if (url == null || !string.Equals(url.Host, Host, StringComparison.OrdinalIgnoreCase))
                return base.ShouldInterceptRequest(view, request);

            var path = (url.Path ?? "/").TrimStart('/');
            if (path.StartsWith("assets/")) path = path.Substring("assets/".Length);
            if (path.Length == 0) path = "index.html";

            try
            {
                var stream = activity.Assets!.Open("www/" + path);
                var headers = new Dictionary<string, string> { ["Access-Control-Allow-Origin"] = "*" };
                return new WebResourceResponse(MimeFor(path), "UTF-8", 200, "OK", headers, stream);
            }
            catch (Exception)
            {
                return new WebResourceResponse("text/plain", "UTF-8", 404, "Not Found",
                    new Dictionary<string, string>(), new MemoryStream());
            }
        }

        // Los enlaces externos se abren en el navegador, no dentro de la app.
        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
        {
            var url = request?.Url;
            if (url == null) return false;
            if (string.Equals(url.Host, Host, StringComparison.OrdinalIgnoreCase)) return false;
            try { activity.StartActivity(new Intent(Intent.ActionView, url)); } catch (Exception) { }
            return true;
        }

        static string MimeFor(string path)
        {
            var p = path.ToLowerInvariant();
            if (p.EndsWith(".html")) return "text/html";
            if (p.EndsWith(".js")) return "text/javascript";
            if (p.EndsWith(".css")) return "text/css";
            if (p.EndsWith(".woff2")) return "font/woff2";
            if (p.EndsWith(".png")) return "image/png";
            if (p.EndsWith(".json")) return "application/json";
            return "application/octet-stream";
        }
    }

    // ---- selector de archivos del sistema para "Agregar PDF" ----
    class ChromeClient : WebChromeClient
    {
        readonly MainActivity activity;
        public ChromeClient(MainActivity a) { activity = a; }

        public override bool OnShowFileChooser(WebView? webView, IValueCallback? filePathCallback, FileChooserParams? fileChooserParams)
        {
            activity.pendingChooser?.OnReceiveValue(null);
            activity.pendingChooser = filePathCallback;

            var intent = new Intent(Intent.ActionOpenDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType("application/pdf");
            intent.PutExtra(Intent.ExtraAllowMultiple, true);
            try
            {
                activity.StartActivityForResult(intent, FileRequest);
            }
            catch (Exception)
            {
                activity.pendingChooser?.OnReceiveValue(null);
                activity.pendingChooser = null;
                return false;
            }
            return true;
        }
    }

    // ---- funciones nativas que llama la página: pantalla encendida y compartir ----
    class Bridge : Java.Lang.Object
    {
        readonly MainActivity activity;
        public Bridge(MainActivity a) { activity = a; }

        [JavascriptInterface]
        [Export("keepAwake")]
        public void KeepAwake(bool on)
        {
            activity.RunOnUiThread(() =>
            {
                if (on) activity.Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
                else activity.Window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
            });
        }

        [JavascriptInterface]
        [Export("setBarColor")]
        public void SetBarColor(string hex, bool lightBackground)
        {
            activity.RunOnUiThread(() =>
            {
                try
                {
                    activity.root?.SetBackgroundColor(Color.ParseColor(hex));
                    if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
                    {
                        int mask = (int)(WindowInsetsControllerAppearance.LightStatusBars | WindowInsetsControllerAppearance.LightNavigationBars);
                        activity.Window?.InsetsController?.SetSystemBarsAppearance(lightBackground ? mask : 0, mask);
                    }
                }
                catch (Exception) { }
            });
        }

        [JavascriptInterface]
        [Export("shareText")]
        public void ShareText(string title, string text)
        {
            activity.RunOnUiThread(() =>
            {
                var send = new Intent(Intent.ActionSend);
                send.SetType("text/plain");
                send.PutExtra(Intent.ExtraSubject, title);
                send.PutExtra(Intent.ExtraText, text);
                activity.StartActivity(Intent.CreateChooser(send, "Compartir subrayados"));
            });
        }
    }

    class InsetsListener : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets? OnApplyWindowInsets(View? v, WindowInsets? insets)
        {
            if (v == null || insets == null) return insets;
            if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            {
                var bars = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout());
                var ime = insets.GetInsets(WindowInsets.Type.Ime());
                v.SetPadding(bars.Left, bars.Top, bars.Right, Math.Max(bars.Bottom, ime.Bottom));
            }
            return insets;
        }
    }

    class BackResult : Java.Lang.Object, IValueCallback
    {
        readonly MainActivity activity;
        public BackResult(MainActivity a) { activity = a; }

        public void OnReceiveValue(Java.Lang.Object? value)
        {
            var handled = value?.ToString() == "true";
            if (!handled) activity.MoveTaskToBack(true);
        }
    }
}
