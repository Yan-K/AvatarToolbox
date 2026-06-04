using System;
using System.Reflection;
using UnityEngine;

namespace YanK
{
	/// <summary>
	/// Reflection-based blocker for third-party camera controller injections.
	///
	/// Some tools poll active cameras every fraction of a second and force-attach a
	/// <c>GameViewCameraController</c> MonoBehaviour onto whichever camera they
	/// consider the "best game view camera". When that camera happens to be ours,
	/// two scripts end up writing the camera transform every frame and the user
	/// sees uncontrollable jitter and rotation jumps.
	///
	/// This helper:
	///   1. Disables the auto-attach manager so the polling stops.
	///   2. Removes any already-attached controller component from our cameras.
	///
	/// Everything is done via reflection so we don't take a hard compile-time
	/// dependency. If the types don't exist (tool not installed),
	/// the calls become no-ops.
	/// </summary>
	internal static class SceneControllerExternalBlocker
	{
		// Type names we look for. Add more here if other tools start injecting
		// their own camera controllers onto our cameras.
		private static readonly string[] AutoAttachTypeNames = { "AutoAttachGameViewCameraController" };
		private static readonly string[] ControllerTypeNames = { "GameViewCameraController" };

		private static Type _autoAttachType;
		private static Type _controllerType;
		private static bool _typesResolved;

		private static void ResolveTypes()
		{
			if (_typesResolved) return;
			_typesResolved = true;

			foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
			{
				try
				{
					if (_autoAttachType == null)
					{
						foreach (var n in AutoAttachTypeNames)
						{
							var t = asm.GetType(n, false);
							if (t != null && typeof(MonoBehaviour).IsAssignableFrom(t))
							{
								_autoAttachType = t;
								break;
							}
						}
					}
					if (_controllerType == null)
					{
						foreach (var n in ControllerTypeNames)
						{
							var t = asm.GetType(n, false);
							if (t != null && typeof(MonoBehaviour).IsAssignableFrom(t))
							{
								_controllerType = t;
								break;
							}
						}
					}
					if (_autoAttachType != null && _controllerType != null) return;
				}
				catch
				{
					// ReflectionTypeLoadException etc. — ignore and continue.
				}
			}
		}

		public static void Tick(SceneController sc)
		{
			if (sc == null) return;
			ResolveTypes();

			DisableAutoAttachManagers();
			StripControllerFrom(sc.defaultCameraGo);
			StripControllerFrom(sc.freeFlyCamera);
			if (sc.sceneCustomCameras != null)
			{
				for (int i = 0; i < sc.sceneCustomCameras.Count; i++)
				{
					var e = sc.sceneCustomCameras[i];
					if (e != null) StripControllerFrom(e.gameObject);
				}
			}
		}

		private static void DisableAutoAttachManagers()
		{
			if (_autoAttachType == null) return;
			var found = UnityEngine.Object.FindObjectsOfType(_autoAttachType, true);
			if (found == null) return;
			for (int i = 0; i < found.Length; i++)
			{
				// Disabling (rather than destroying) is reversible: the user can
				// flip our toggle off and normal operation resumes.
				if (found[i] is Behaviour b && b.enabled) b.enabled = false;
			}
		}

		private static void StripControllerFrom(GameObject go)
		{
			if (go == null || _controllerType == null) return;
			var comp = go.GetComponent(_controllerType);
			if (comp == null) return;
			if (Application.isPlaying) UnityEngine.Object.Destroy(comp);
			else UnityEngine.Object.DestroyImmediate(comp);
		}
	}
}
