using System;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Networking;

namespace YanK
{
	/// <summary>
	/// Checks GitHub releases + the community VPM listing for a newer Yan-K Avatar Toolbox
	/// version than the one currently installed, and drives the yellow "Update" button shown
	/// in every Yan-K tool header (<see cref="YanKEditorWindow.DrawHeader"/> and
	/// <see cref="YanKInspectorGUI.DrawHeaderRow"/>).
	/// </summary>
	public static class YanKUpdateChecker
	{
		private const string GithubReleaseUrl = "https://api.github.com/repos/Yan-K/AvatarToolbox/releases/latest";
		private const string VpmIndexUrl      = "https://xtlcdn.github.io/vpm/index.json";

		private const string PrefLastCheckTicks = "YAT_LastUpdateCheckTicks";
		private const string PrefCachedLatest   = "YAT_CachedLatestVersion";
		private const string PrefSkippedVersion = "YAT_SkippedUpdateVersion";

		private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
		// Minimum gap between window-open-triggered checks, purely to avoid firing duplicate
		// near-simultaneous requests when several Yan-K windows open together (saved layout, etc.).
		private static readonly TimeSpan WindowOpenDebounce = TimeSpan.FromSeconds(10);

		public enum CheckState { Idle, Checking, Done, Offline, Failed }

		public static CheckState State { get; private set; } = CheckState.Idle;

		private static string _currentVersion;
		private static string _latestVersion;
		private static bool   _requestedThisSession;

		private static UnityWebRequest _vpmRequest;
		private static UnityWebRequest _githubRequest;
		private static bool   _vpmDone;
		private static bool   _githubDone;
		private static string _vpmVersion;
		private static string _githubVersion;

		// ------------------------------------------------------------------
		// Public API
		// ------------------------------------------------------------------

		public static string CurrentVersion => _currentVersion ??= ResolveCurrentVersion();

		public static string LatestVersion => _latestVersion;

		public static bool UpdateAvailable =>
			!string.IsNullOrEmpty(_latestVersion) &&
			!string.IsNullOrEmpty(CurrentVersion) &&
			IsNewer(_latestVersion, CurrentVersion);

		/// <summary>
		/// Call once per OnGUI from any tool header. Cheap no-op after the first call this
		/// session (or after the 6h throttle window elapses) — safe to call every frame.
		/// </summary>
		public static void EnsureChecked()
		{
			// Surface a cached result immediately so the button can show without waiting on network.
			if (string.IsNullOrEmpty(_latestVersion))
			{
				string cached = EditorPrefs.GetString(PrefCachedLatest, "");
				if (!string.IsNullOrEmpty(cached)) _latestVersion = cached;
			}

			if (_requestedThisSession) return;
			_requestedThisSession = true;

			long lastTicks = 0;
			long.TryParse(EditorPrefs.GetString(PrefLastCheckTicks, "0"), out lastTicks);
			if (lastTicks != 0)
			{
				var elapsed = DateTime.UtcNow - new DateTime(lastTicks, DateTimeKind.Utc);
				if (elapsed < CheckInterval) return; // still fresh — rely on the cached value above
			}

			// User explicitly asked: if offline, don't attempt the check at all.
			if (Application.internetReachability == NetworkReachability.NotReachable)
			{
				State = CheckState.Offline;
				return;
			}

			StartCheck();
		}

		/// <summary>
		/// Call once from a tool window's OnEnable (window opened, reopened, or restored after a
		/// domain reload). Forces a fresh check regardless of the 6h throttle used by
		/// <see cref="EnsureChecked"/> — reopening a Yan-K tool should always reflect the latest
		/// release — while still avoiding duplicate concurrent requests and respecting the offline
		/// check. A short debounce prevents redundant requests when multiple Yan-K windows open
		/// back-to-back (e.g. a saved Editor layout).
		/// </summary>
		public static void CheckOnWindowOpen()
		{
			// Surface a cached result immediately so the button can show without waiting on network.
			if (string.IsNullOrEmpty(_latestVersion))
			{
				string cached = EditorPrefs.GetString(PrefCachedLatest, "");
				if (!string.IsNullOrEmpty(cached)) _latestVersion = cached;
			}

			// Stop the per-OnGUI EnsureChecked() from also firing right after this.
			_requestedThisSession = true;

			if (State == CheckState.Checking) return; // a check is already in flight

			long lastTicks = 0;
			long.TryParse(EditorPrefs.GetString(PrefLastCheckTicks, "0"), out lastTicks);
			if (lastTicks != 0 && DateTime.UtcNow - new DateTime(lastTicks, DateTimeKind.Utc) < WindowOpenDebounce)
				return;

			if (Application.internetReachability == NetworkReachability.NotReachable)
			{
				State = CheckState.Offline;
				return;
			}

			StartCheck();
		}

		/// <summary>True when the yellow Update button should be drawn.</summary>
		public static bool ShouldShowButton()
		{
			if (!UpdateAvailable) return false;
			string skipped = EditorPrefs.GetString(PrefSkippedVersion, "");
			return skipped != _latestVersion;
		}

		/// <summary>
		/// Hides the button until a version newer than the current <see cref="LatestVersion"/>
		/// is found — skip only ever applies to the one version the user saw.
		/// </summary>
		public static void SkipCurrent()
		{
			if (string.IsNullOrEmpty(_latestVersion)) return;
			EditorPrefs.SetString(PrefSkippedVersion, _latestVersion);
		}

		/// <summary>Draws the yellow "Update" button; caller is responsible for gating with <see cref="ShouldShowButton"/>.</summary>
		public static void DrawUpdateButton()
		{
			var prevColor = GUI.backgroundColor;
			GUI.backgroundColor = new Color(1f, 0.82f, 0.15f);
			if (GUILayout.Button(
				new GUIContent(
					YanKLocalization.L("updateButton", "Update"),
					YanKLocalization.L("updateButtonTooltip", "A newer version is available. Click for download options.")),
				EditorStyles.miniButton, GUILayout.Width(64)))
			{
				YanKUpdatePopup.ShowPopup();
			}
			GUI.backgroundColor = prevColor;
			GUILayout.Space(6);
		}

		// ------------------------------------------------------------------
		// Networking
		// ------------------------------------------------------------------

		private static void StartCheck()
		{
			State = CheckState.Checking;
			_vpmDone = false;
			_githubDone = false;
			_vpmVersion = null;
			_githubVersion = null;

			_vpmRequest = UnityWebRequest.Get(VpmIndexUrl);
			_vpmRequest.SendWebRequest();

			_githubRequest = UnityWebRequest.Get(GithubReleaseUrl);
			_githubRequest.SetRequestHeader("User-Agent", "YanK-AvatarToolbox"); // GitHub rejects requests with no User-Agent
			_githubRequest.SendWebRequest();

			EditorApplication.update += PollRequests;
		}

		private static void PollRequests()
		{
			if (_vpmRequest != null && _vpmRequest.isDone && !_vpmDone)
			{
				_vpmDone = true;
				if (_vpmRequest.result == UnityWebRequest.Result.Success)
				{
					try { _vpmVersion = ParseVpmVersion(_vpmRequest.downloadHandler.text); }
					catch (Exception e) { Debug.LogWarning($"[YAT] Failed to parse VPM listing: {e.Message}"); }
				}
				_vpmRequest.Dispose();
				_vpmRequest = null;
			}

			if (_githubRequest != null && _githubRequest.isDone && !_githubDone)
			{
				_githubDone = true;
				if (_githubRequest.result == UnityWebRequest.Result.Success)
				{
					try { _githubVersion = ParseGithubVersion(_githubRequest.downloadHandler.text); }
					catch (Exception e) { Debug.LogWarning($"[YAT] Failed to parse GitHub release: {e.Message}"); }
				}
				_githubRequest.Dispose();
				_githubRequest = null;
			}

			if (_vpmDone && _githubDone)
			{
				EditorApplication.update -= PollRequests;
				FinishCheck();
			}
		}

		private static void FinishCheck()
		{
			// Take the higher of the two — the community VPM listing has been observed to lag
			// behind fresh GitHub releases, while GitHub's API can occasionally rate-limit.
			string best = null;
			if (!string.IsNullOrEmpty(_vpmVersion)) best = _vpmVersion;
			if (!string.IsNullOrEmpty(_githubVersion) && (best == null || IsNewer(_githubVersion, best)))
				best = _githubVersion;

			EditorPrefs.SetString(PrefLastCheckTicks, DateTime.UtcNow.Ticks.ToString());

			if (best != null)
			{
				_latestVersion = best;
				EditorPrefs.SetString(PrefCachedLatest, best);
				State = CheckState.Done;
			}
			else
			{
				State = CheckState.Failed;
			}

			// Repaint any open Yan-K windows so the button appears without needing user input.
			InternalEditorUtility.RepaintAllViews();
		}

		// ------------------------------------------------------------------
		// Parsing
		// ------------------------------------------------------------------

		[Serializable]
		private class GithubReleaseResponse { public string tag_name; }

		private static string ParseGithubVersion(string json)
		{
			var release = JsonUtility.FromJson<GithubReleaseResponse>(json);
			string tag = release?.tag_name;
			if (string.IsNullOrEmpty(tag)) return null;
			return tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag.Substring(1) : tag;
		}

		// Matches entries like `"name":"com.yank.avatartoolbox","displayName":"...","version":"1.6.2"`
		// in the VPM listing JSON. Dynamic version keys make full JSON parsing overkill here;
		// pre-release versions (e.g. "1.6.2-beta.1") are naturally skipped since the pattern
		// requires the closing quote to immediately follow the 3rd numeric component.
		private static readonly Regex VpmVersionRegex = new Regex(
			"\"name\"\\s*:\\s*\"com\\.yank\\.avatartoolbox\"[^}]*?\"version\"\\s*:\\s*\"([0-9]+\\.[0-9]+\\.[0-9]+)\"",
			RegexOptions.Singleline);

		private static string ParseVpmVersion(string json)
		{
			string best = null;
			foreach (Match m in VpmVersionRegex.Matches(json))
			{
				string v = m.Groups[1].Value;
				if (best == null || IsNewer(v, best)) best = v;
			}
			return best;
		}

		private static string ResolveCurrentVersion()
		{
			try
			{
				var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(YanKUpdateChecker).Assembly);
				if (info != null && !string.IsNullOrEmpty(info.version)) return info.version;
			}
			catch { /* fall through to manual read */ }

			try
			{
				var asset = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.yank.avatartoolbox/package.json");
				if (asset != null)
				{
					var m = Regex.Match(asset.text, "\"version\"\\s*:\\s*\"([0-9.]+)\"");
					if (m.Success) return m.Groups[1].Value;
				}
			}
			catch { /* ignore */ }

			return null;
		}

		// ------------------------------------------------------------------
		// Version comparison (3-digit major.minor.fix)
		// ------------------------------------------------------------------

		public static bool IsNewer(string latest, string current)
		{
			if (!TryParseVersion(latest, out var l)) return false;
			if (!TryParseVersion(current, out var c)) return false;
			if (l.major != c.major) return l.major > c.major;
			if (l.minor != c.minor) return l.minor > c.minor;
			return l.fix > c.fix;
		}

		private static bool TryParseVersion(string s, out (int major, int minor, int fix) v)
		{
			v = (0, 0, 0);
			if (string.IsNullOrEmpty(s)) return false;
			if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
			int cut = s.IndexOfAny(new[] { '-', '+' });
			if (cut >= 0) s = s.Substring(0, cut);
			var parts = s.Split('.');
			if (parts.Length == 0) return false;
			int major = 0, minor = 0, fix = 0;
			int.TryParse(parts[0], out major);
			if (parts.Length > 1) int.TryParse(parts[1], out minor);
			if (parts.Length > 2) int.TryParse(parts[2], out fix);
			v = (major, minor, fix);
			return true;
		}
	}
}
