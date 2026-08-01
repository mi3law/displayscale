using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace DisplayScale
{
    /// <summary>
    /// Serves the settings page to the browser from inside the tray process.
    ///
    /// HttpListener binds loopback prefixes without a URL ACL, so this needs no
    /// elevation and adds no process, service, or dependency -- it is the same exe.
    ///
    /// It only listens on 127.0.0.1, on an ephemeral port, and every request must
    /// carry a per-session token. The server can rewrite the config and change
    /// display scaling, so it is started on demand rather than kept open, and shuts
    /// itself down once the page has been gone for a while.
    /// </summary>
    internal class ConfigServer
    {
        const int IdleShutdownMinutes = 20;

        readonly TrayContext _app;
        readonly Control _ui;
        readonly JavaScriptSerializer _json = new JavaScriptSerializer();

        HttpListener _listener;
        Thread _thread;
        System.Threading.Timer _idleTimer; // qualified: System.Windows.Forms.Timer is also in scope
        string _token;
        string _url;
        DateTime _lastRequest;

        public ConfigServer(TrayContext app, Control uiThread)
        {
            _app = app;
            _ui = uiThread;
        }

        public bool Running { get { return _listener != null && _listener.IsListening; } }
        public string Url { get { return _url; } }

        public bool Start(out string error)
        {
            error = null;
            if (Running) return true;

            var rng = new Random();
            for (int attempt = 0; attempt < 16; attempt++)
            {
                int port = 49152 + rng.Next(0, 16000);
                var listener = new HttpListener();
                listener.Prefixes.Add("http://127.0.0.1:" + port + "/");

                try { listener.Start(); }
                catch
                {
                    try { listener.Close(); }
                    catch { }
                    continue; // port taken; try another
                }

                _listener = listener;
                _token = Guid.NewGuid().ToString("N");
                _url = "http://127.0.0.1:" + port + "/?t=" + _token;
                _lastRequest = DateTime.UtcNow;

                _thread = new Thread(Loop);
                _thread.IsBackground = true;
                _thread.Name = "displayscale-settings";
                _thread.Start();

                _idleTimer = new System.Threading.Timer(CheckIdle, null, 60000, 60000);
                return true;
            }

            error = "could not bind a loopback port for the settings page";
            return false;
        }

        public void Stop()
        {
            if (_idleTimer != null) { _idleTimer.Dispose(); _idleTimer = null; }
            if (_listener == null) return;
            try { _listener.Stop(); _listener.Close(); }
            catch { }
            _listener = null;
            _url = null;
        }

        void CheckIdle(object state)
        {
            if (!Running) return;
            if ((DateTime.UtcNow - _lastRequest).TotalMinutes < IdleShutdownMinutes) return;
            Stop();
        }

        void Loop()
        {
            while (_listener != null && _listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { return; } // listener stopped
                try { Handle(ctx); }
                catch
                {
                    try { ctx.Response.StatusCode = 500; }
                    catch { }
                }
                finally
                {
                    try { ctx.Response.Close(); }
                    catch { }
                }
            }
        }

        void Handle(HttpListenerContext ctx)
        {
            _lastRequest = DateTime.UtcNow;
            HttpListenerRequest req = ctx.Request;
            HttpListenerResponse res = ctx.Response;

            res.Headers["Cache-Control"] = "no-store";
            res.Headers["X-Content-Type-Options"] = "nosniff";

            string path = req.Url.AbsolutePath;
            if (path == "/favicon.ico") { res.StatusCode = 404; return; }

            string token = req.QueryString["t"];
            if (string.IsNullOrEmpty(token)) token = req.Headers["X-Token"];
            if (!string.Equals(token, _token, StringComparison.Ordinal))
            {
                res.StatusCode = 403;
                WriteText(res, "forbidden");
                return;
            }

            if (path == "/")
            {
                WriteHtml(res, LoadPage());
                return;
            }
            if (path == "/api/state")
            {
                WriteJson(res, BuildState());
                return;
            }
            if (path == "/api/device")
            {
                WriteJson(res, BuildDevice());
                return;
            }
            if (path == "/api/apply" && req.HttpMethod == "POST")
            {
                var body = ReadJson(req);
                string name = Str(body, "profile");
                Invoke(delegate { _app.ApplyProfileFromUi(name); });
                WriteJson(res, BuildState());
                return;
            }
            if (path == "/api/save" && req.HttpMethod == "POST")
            {
                var body = ReadJson(req);
                string error = null;
                Invoke(delegate { error = _app.SaveConfigFromUi(body); });

                var result = new Dictionary<string, object>();
                result["ok"] = error == null;
                if (error != null) result["error"] = error;
                else result["state"] = BuildState();
                WriteJson(res, result);
                return;
            }

            res.StatusCode = 404;
            WriteText(res, "not found");
        }

        /// <summary>
        /// Config and display changes have to happen on the UI thread: they touch the
        /// tray icon and the hotkey registration, both of which are thread-affine.
        /// </summary>
        void Invoke(Action action)
        {
            if (_ui.InvokeRequired) _ui.Invoke(action);
            else action();
        }

        // ---- payloads -------------------------------------------------------

        Dictionary<string, object> BuildState()
        {
            var state = new Dictionary<string, object>();
            Config cfg = _app.CurrentConfig;

            var status = new Dictionary<string, object>();
            status["profile"] = _app.CurrentProfileName;
            status["paused"] = _app.IsPaused;
            status["held"] = _app.IsHeld;
            status["hotkeyActive"] = _app.ActiveHotkeyText;
            state["status"] = status;

            var monitors = new List<object>();
            try
            {
                foreach (var m in Dpi.Enumerate())
                {
                    var entry = new Dictionary<string, object>();
                    entry["name"] = m.FriendlyName;
                    entry["gdi"] = m.GdiName;
                    entry["current"] = m.CurrentPercent;
                    entry["recommended"] = m.RecommendedPercent;
                    entry["max"] = m.MaxPercent;
                    entry["available"] = m.AvailablePercents();
                    monitors.Add(entry);
                }
            }
            catch { }
            state["monitors"] = monitors;

            var settings = new Dictionary<string, object>();
            settings["min_events"] = cfg.MinEvents;
            settings["evidence_window_ms"] = cfg.EvidenceWindowMs;
            settings["cooldown_ms"] = cfg.CooldownMs;
            settings["hotkey"] = cfg.Hotkey;
            settings["log"] = cfg.Log;
            state["settings"] = settings;

            var profiles = new List<object>();
            foreach (var p in cfg.Profiles)
            {
                var entry = new Dictionary<string, object>();
                entry["name"] = p.Name;

                var matches = new List<object>();
                foreach (string m in p.Match)
                {
                    var mv = new Dictionary<string, object>();
                    mv["value"] = m;
                    mv["label"] = RawInputWatcher.Describe(m);
                    matches.Add(mv);
                }
                entry["match"] = matches;

                var scales = new List<object>();
                foreach (var s in p.Scales)
                {
                    var sv = new Dictionary<string, object>();
                    sv["key"] = s.MonitorKey;
                    sv["percent"] = s.Percent;
                    scales.Add(sv);
                }
                entry["scales"] = scales;

                profiles.Add(entry);
            }
            state["profiles"] = profiles;

            return state;
        }

        Dictionary<string, object> BuildDevice()
        {
            var d = new Dictionary<string, object>();
            string name = _app.LastDeviceName;
            d["name"] = name;
            d["label"] = string.IsNullOrEmpty(name) ? null : RawInputWatcher.Describe(name);
            d["suggestion"] = string.IsNullOrEmpty(name) ? null : SuggestMatch(name);
            return d;
        }

        /// <summary>
        /// Narrow a full device path down to the part that identifies the hardware
        /// rather than the port it happens to be on: the Bluetooth product id, or the
        /// USB vendor+product pair. Anything longer would break if it moved ports.
        /// </summary>
        public static string SuggestMatch(string deviceName)
        {
            string lower = deviceName.ToLowerInvariant();

            int ble = lower.IndexOf("_pid&");
            if (ble >= 0)
            {
                int end = lower.IndexOf('_', ble + 5);
                if (end > ble) return lower.Substring(ble, end - ble + 1);
            }

            int vid = lower.IndexOf("vid_");
            if (vid >= 0)
            {
                int pid = lower.IndexOf("pid_", vid);
                if (pid > vid)
                {
                    int end = pid + 4;
                    while (end < lower.Length && Uri.IsHexDigit(lower[end])) end++;
                    return lower.Substring(vid, end - vid);
                }
            }

            return lower;
        }

        // ---- plumbing -------------------------------------------------------

        Dictionary<string, object> ReadJson(HttpListenerRequest req)
        {
            using (var reader = new StreamReader(req.InputStream, Encoding.UTF8))
            {
                string body = reader.ReadToEnd();
                if (string.IsNullOrEmpty(body)) return new Dictionary<string, object>();
                var parsed = _json.DeserializeObject(body) as Dictionary<string, object>;
                return parsed ?? new Dictionary<string, object>();
            }
        }

        public static string Str(Dictionary<string, object> map, string key)
        {
            object v;
            if (map == null || !map.TryGetValue(key, out v) || v == null) return null;
            return v.ToString();
        }

        public static int Int(Dictionary<string, object> map, string key, int fallback)
        {
            object v;
            if (map == null || !map.TryGetValue(key, out v) || v == null) return fallback;
            try { return Convert.ToInt32(v); }
            catch { return fallback; }
        }

        public static bool Bool(Dictionary<string, object> map, string key, bool fallback)
        {
            object v;
            if (map == null || !map.TryGetValue(key, out v) || v == null) return fallback;
            try { return Convert.ToBoolean(v); }
            catch { return fallback; }
        }

        static string LoadPage()
        {
            var asm = Assembly.GetExecutingAssembly();
            using (Stream s = asm.GetManifestResourceStream("DisplayScale.settings.html"))
            {
                if (s == null) return "<h1>settings.html resource missing from the build</h1>";
                using (var reader = new StreamReader(s, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
        }

        void WriteJson(HttpListenerResponse res, object payload)
        {
            Write(res, "application/json; charset=utf-8", _json.Serialize(payload));
        }

        void WriteHtml(HttpListenerResponse res, string html)
        {
            Write(res, "text/html; charset=utf-8", html);
        }

        void WriteText(HttpListenerResponse res, string text)
        {
            Write(res, "text/plain; charset=utf-8", text);
        }

        void Write(HttpListenerResponse res, string contentType, string body)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            res.ContentType = contentType;
            res.ContentLength64 = bytes.Length;
            res.OutputStream.Write(bytes, 0, bytes.Length);
        }
    }
}
