using UnityEngine;

namespace YanK
{
	internal static class SceneControllerInput
	{
		private static bool _rmbWasDown;
		// On RMB press Unity locks the cursor; the OS pointer takes a couple of
		// frames to settle, during which Mouse X/Y reports huge garbage deltas.
		private static int _swallowMouseFrames;
		// Hard cap on per-frame mouse delta — any residual spike past the
		// swallow window can't yank the camera more than a few degrees.
		private const float MaxMouseDeltaPerFrame = 8f;

		public static void HandleInput(SceneController sc, float dt)
		{
			if (sc == null) return;
			if (!Application.isPlaying) return;

			// Off mode: do NOT read input, do NOT touch the cursor, do NOT write any
			// camera transform — leave the field clear for external camera scripts
			// (e.g. AvaPo's GameViewCameraController) to drive things.
			if (sc.GetEffectiveCameraMode() == CameraControlMode.Off)
			{
				if (_rmbWasDown)
				{
					Cursor.lockState = CursorLockMode.None;
					Cursor.visible = true;
					_rmbWasDown = false;
				}
				_swallowMouseFrames = 0;
				return;
			}

			bool rmb = Input.GetMouseButton(1);

			if (rmb && !_rmbWasDown)
			{
				Cursor.lockState = CursorLockMode.Locked;
				Cursor.visible = false;
				_swallowMouseFrames = 3;
			}
			else if (!rmb && _rmbWasDown)
			{
				Cursor.lockState = CursorLockMode.None;
				Cursor.visible = true;
			}
			_rmbWasDown = rmb;

			Camera cam = sc.GetActiveCamera();
			if (cam == null) return;

			float mx = Input.GetAxisRaw("Mouse X");
			float my = Input.GetAxisRaw("Mouse Y");
			if (_swallowMouseFrames > 0)
			{
				mx = 0f;
				my = 0f;
				_swallowMouseFrames--;
			}
			else
			{
				// Defensive: clamp any residual spike (rare but happens on alt-tab / focus changes).
				mx = Mathf.Clamp(mx, -MaxMouseDeltaPerFrame, MaxMouseDeltaPerFrame);
				my = Mathf.Clamp(my, -MaxMouseDeltaPerFrame, MaxMouseDeltaPerFrame);
			}
			float wheel = Input.GetAxis("Mouse ScrollWheel");
			float h = (Input.GetKey(KeyCode.D) ? 1f : 0f) - (Input.GetKey(KeyCode.A) ? 1f : 0f);
			float v = (Input.GetKey(KeyCode.W) ? 1f : 0f) - (Input.GetKey(KeyCode.S) ? 1f : 0f);
			float y = (Input.GetKey(KeyCode.E) ? 1f : 0f) - (Input.GetKey(KeyCode.Q) ? 1f : 0f);

			// Shift = 2× speed boost — applies to both avatar movement and camera free-fly.
			bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
			float speedMult = shift ? 2f : 1f;

		bool mmb = Input.GetMouseButton(2);
		bool lmb = Input.GetMouseButton(0);

		if (sc.GetEffectiveCameraMode() == CameraControlMode.Orbit)
			HandleOrbit(sc, cam, dt, rmb, mmb, mx, my, wheel, h, v, y, speedMult);
			else
				HandleFreeFly(sc, cam, dt, rmb, mmb, lmb, mx, my, wheel, h, v, y, speedMult);
		}

		private static void HandleOrbit(SceneController sc, Camera cam, float dt, bool rmb, bool mmb,
			float mx, float my, float wheel, float h, float v, float y, float speedMult)
		{
			if (mmb && (mx != 0f || my != 0f))
			{
				// MMB held: pan the camera pivot offset (persists across LateUpdate).
				float panSpeed = Mathf.Max(0.2f, sc.cameraDistance) * 0.005f * sc.mouseSensitivity;
				Transform ct = cam.transform;
				Vector3 delta = -(ct.right * mx + ct.up * my) * panSpeed;
				sc.cameraPivotOffset += delta;
				if (sc.cameraPivot != null) sc.cameraPivot.position += delta;
			}

			if (rmb)
			{
				sc.cameraYaw += mx * sc.mouseSensitivity;
				// Mouse up = orbit rises (camera moves above avatar).
				// invertMouseY flips this back for users who prefer the opposite.
				sc.cameraPitch += my * sc.mouseSensitivity * (sc.invertMouseY ? -1f : 1f);
			}

			sc.cameraYaw = Mathf.Repeat(sc.cameraYaw + 180f, 360f) - 180f;
			sc.cameraPitch = Mathf.Clamp(sc.cameraPitch, -89f, 89f);

			if (!Mathf.Approximately(wheel, 0f))
			{
				// Distance-proportional zoom — scrolling feels uniform at any distance
				// and small wheel deltas no longer over-correct (reduces perceived jitter).
				float step = wheel * Mathf.Max(0.2f, sc.cameraDistance) * 0.9f;
				sc.cameraDistance = Mathf.Clamp(sc.cameraDistance - step, 0.2f, 20f);
			}

			if (h != 0f || v != 0f || y != 0f)
			{
				Transform ct = cam.transform;
				Vector3 forward = ct.forward; forward.y = 0f; forward.Normalize();
				Vector3 right = ct.right; right.y = 0f; right.Normalize();
				Vector3 delta = (right * h + forward * v) * sc.moveSpeed * speedMult * dt
				                + Vector3.up * (y * sc.verticalSpeed * speedMult * dt);

				if (sc.avatarRoot != null)
				{
					sc.avatarHomePosition += delta;
					sc.avatarRoot.transform.position += delta;
				}
				if (sc.cameraPivot != null) sc.cameraPivot.position += delta;
			}
		}

		// Free-fly: RMB = look (yaw/pitch), LMB+RMB = roll (mouse X),
		// MMB = screen-space pan, scroll = dolly forward/back.
		private static void HandleFreeFly(SceneController sc, Camera cam, float dt,
			bool rmb, bool mmb, bool lmb,
			float mx, float my, float wheel,
			float h, float v, float y, float speedMult)
		{
			Transform ct = cam.transform;

			// ---- MMB screen-space pan (works regardless of RMB) ----
			if (mmb && (mx != 0f || my != 0f))
			{
				float panSpeed = Mathf.Max(0.2f, sc.moveSpeed) * 0.02f * sc.mouseSensitivity;
				ct.position += -(ct.right * mx + ct.up * my) * panSpeed;
			}

			// ---- Scroll wheel dolly along forward ----
			if (!Mathf.Approximately(wheel, 0f))
			{
				float dollyStep = wheel * Mathf.Max(0.5f, sc.moveSpeed) * 5f * speedMult;
				ct.position += ct.forward * dollyStep;
			}

			if (rmb)
			{
				if (lmb)
				{
					// Both buttons held → roll around local forward via mouse X.
					// Yaw/pitch suspended so the user can dial in roll cleanly.
					if (mx != 0f)
					{
						float rollDelta = -mx * sc.mouseSensitivity;
						ct.rotation = Quaternion.AngleAxis(rollDelta, ct.forward) * ct.rotation;
					}
				}
				else if (mx != 0f || my != 0f)
				{
					// Quaternion-delta look — see history for why we never decompose Euler here.
					float yawDelta = mx * sc.mouseSensitivity;
					float pitchDelta = -my * sc.mouseSensitivity * (sc.invertMouseY ? -1f : 1f);

					float currentPitch = -Mathf.Asin(Mathf.Clamp(ct.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
					float newPitch = Mathf.Clamp(currentPitch + pitchDelta, -89f, 89f);
					pitchDelta = newPitch - currentPitch;

					ct.rotation = Quaternion.AngleAxis(yawDelta, Vector3.up) * ct.rotation;
					ct.rotation = Quaternion.AngleAxis(pitchDelta, ct.right) * ct.rotation;
				}

				if (h != 0f || v != 0f || y != 0f)
				{
					Vector3 delta = (ct.right * h + ct.forward * v) * sc.moveSpeed * speedMult * dt
					                + Vector3.up * (y * sc.verticalSpeed * speedMult * dt);
					ct.position += delta;
				}
			}
			else
			{
				// Not holding RMB: WASD/QE moves the avatar, or the camera if no avatar.
				if (h != 0f || v != 0f || y != 0f)
				{
					Vector3 forward = ct.forward; forward.y = 0f; forward.Normalize();
					Vector3 right = ct.right; right.y = 0f; right.Normalize();
					Vector3 delta = (right * h + forward * v) * sc.moveSpeed * speedMult * dt
					                + Vector3.up * (y * sc.verticalSpeed * speedMult * dt);

					if (sc.avatarRoot != null)
					{
						sc.avatarHomePosition += delta;
						sc.avatarRoot.transform.position += delta;
						if (sc.cameraPivot != null) sc.cameraPivot.position += delta;
					}
					else
					{
						ct.position += delta;
					}
				}
			}
		}
	}
}
