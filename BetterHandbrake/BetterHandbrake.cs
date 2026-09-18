using System.Collections.Generic;
using TLDLoader;
using UnityEngine;

namespace BetterHandbrake
{
	public class BetterHandbrake : Mod
	{
		public override string ID => "M_BetterHandbrake";
		public override string Name => "BetterHandbrake";
		public override string Author => "M-";
		public override string Version => "1.0.0";
		public override bool LoadInDB => true;

		public override void DbLoad()
		{
			foreach (var item in itemdatabase.d.items)
			{
				if (item.GetComponent<carscript>() != null && item.GetComponent<Handbrake>() == null)
					item.AddComponent<Handbrake>();
			}
		}

		public override void OnLoad()
		{
			foreach (KeyValuePair<int, tosaveitemscript> keyValuePair in savedatascript.d.toSaveStuff)
			{
				if (keyValuePair.Value != null && keyValuePair.Value.GetComponent<carscript>() != null && keyValuePair.Value.GetComponent<Handbrake>() == null)
					keyValuePair.Value.gameObject.AddComponent<Handbrake>();
			}
		}
	}

	public class Handbrake : MonoBehaviour
	{
		private const float Epsilon = 0.001f;
		private const float Gravity = 9.81f;
		private const float MetresPerSecondToKmh = 3.6f;

		// Separates a tap, which toggles the latch, from a hold, which is a momentary pull.
		private const float TapTime = 0.25f;

		// Slow enough that holding gives a progressive slide instead of a snap.
		private const float HoldRate = 1f;

		// Finishes a tap quickly so toggling still feels like vanilla.
		private const float LatchRate = 6f;

		private const float ReleaseRate = 6f;

		// Set above 1 so the wheels lock reliably despite variation between tyres.
		private const float LockFactor = 1.5f;

		// Keeps low-speed manoeuvres and cars parked on slopes stable.
		private const float SlideMinSpeed = 8f;

		// Fades the grip loss in so a handbrake turn at low speed isn't violent.
		private const float SlideFullSpeed = 40f;

		private const float SidewaysGripMult = 0.75f;
		private const float EngageRate = 6f;

		// Recovers slower than it engages so the car settles instead of snapping back to grip.
		private const float GripReleaseRate = 3f;

		private carscript _car;

		// The game rewrites stiffness every frame, so the last known stock value is tracked per wheel.
		private readonly Dictionary<WheelCollider, WheelState> _states = new Dictionary<WheelCollider, WheelState>();

		private float _pull;
		private float _blend;
		private bool _wasPlayer;
		private bool _prevHeld;
		private bool _latched;

		// Lets a tap on a latched brake release it while the end of a hold always releases.
		private bool _latchedBefore;

		private float _pressTime;

		private void Start()
		{
			_car = GetComponent<carscript>();
		}

		private void LateUpdate()
		{
			if (_car == null || _car.whlocked || _car.WHHandBrake == null || _car.WHHandBrake.Count == 0) return;

			// Total speed is used because forward speed drops to zero as the car slides sideways, which would cancel the drift.
			float speedKmh = _car.RB != null ? _car.RB.velocity.magnitude * MetresPerSecondToKmh : 0f;

			UpdatePull();

			// Scaling lock torque by per-wheel load gives heavy vehicles a proportionally stronger brake.
			float load = _car.RB != null ? _car.RB.mass * Gravity / Mathf.Max(1, _car.allWH.Count) : 0f;

			if (_pull > Epsilon || _car.handbrake > Epsilon)
			{
				float stock = _car.maxHandBrake * _car.handbrake;
				foreach (WheelCollider w in _car.WHHandBrake)
				{
					if (w == null) continue;

					// The game's torque pass has already run, so its stock handbrake share is removed before ours is added.
					float baseTorque = Mathf.Max(0f, w.brakeTorque - stock);
					float lockTorque = LockFactor * load * w.radius;
					w.brakeTorque = baseTorque + Mathf.Max(_car.maxHandBrake, lockTorque) * _pull;
				}
			}

			// Scaling by pull and speed keeps taps and low-speed use gentle.
			float speedFactor = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(SlideMinSpeed, SlideFullSpeed, speedKmh));
			float target = _pull * speedFactor;
			float rate = target > _blend ? EngageRate : GripReleaseRate;
			_blend = Mathf.MoveTowards(_blend, target, rate * Time.deltaTime);

			foreach (WheelCollider w in _car.WHHandBrake)
			{
				if (w == null) continue;

				if (!_states.TryGetValue(w, out WheelState state))
				{
					state = new WheelState();
					_states[w] = state;
				}

				WheelFrictionCurve friction = w.sidewaysFriction;

				// Anything other than our last write means the game reset it, so that value becomes the new base.
				if (!state.Modified || !Mathf.Approximately(friction.stiffness, state.Applied))
					state.BaseStiffness = friction.stiffness;

				if (_blend > Epsilon)
				{
					float desired = state.BaseStiffness * Mathf.Lerp(1f, SidewaysGripMult, _blend);
					friction.stiffness = desired;
					w.sidewaysFriction = friction;
					state.Applied = desired;
					state.Modified = true;
				}
				else if (state.Modified)
				{
					// Restored once so stock grip is never left modified.
					friction.stiffness = state.BaseStiffness;
					w.sidewaysFriction = friction;
					state.Modified = false;
				}
			}
		}

		private void UpdatePull()
		{
			bool player = _car.driving2 && (_car.isPlayerDriving || _car.isPlayerDriving2);

			if (settingsscript.s.S.CBenableHandBrake)
			{
				// An analogue axis needs no ramping because the game already provides a smooth value.
				_pull = _car.handbrake;
			}
			else if (player)
			{
				if (!_wasPlayer)
				{
					// Continues from the state the car was left in so a parked brake isn't lost on entry.
					_latched = _car.BhandBrake;
					_prevHeld = false;
				}

				// Mirrors the game's own driving input gate so the brake can't be pulled while looking around or asleep.
				bool canInput = (!inputscript.i.keyLooking || inputscript.i.lean) && mainscript.M.player.mind && !mainscript.M.sleeping;
				bool held = canInput && inputscript.i.GetKey(inputscript.IN.handbrake);

				// Edges are detected here because calling GetKeyDown again could disturb the game's own axis edge tracking.
				if (held && !_prevHeld)
				{
					_pressTime = Time.time;
					_latchedBefore = _latched;
				}
				else if (!held && _prevHeld)
				{
					bool tap = Time.time - _pressTime < TapTime;
					_latched = tap ? !_latchedBefore : false;
				}
				_prevHeld = held;

				// Overwrites the stock toggle, which has already flipped this earlier in the frame.
				_car.BhandBrake = held || _latched;

				// A hold ramps slowly for control while a latched tap finishes quickly to feel like vanilla.
				float target = _car.BhandBrake ? 1f : 0f;
				float rate = target > _pull ? (held ? HoldRate : LatchRate) : ReleaseRate;
				_pull = Mathf.MoveTowards(_pull, target, rate * Time.deltaTime);
			}
			else
			{
				if (_wasPlayer)
				{
					_prevHeld = false;

					// A latched brake stays on like vanilla but a held one must not stay stuck after the player leaves.
					_car.BhandBrake = _latched;
				}

				// Parked and remote-driven cars keep the stock state.
				_pull = _car.handbrake;
			}

			_wasPlayer = player;
		}

		private class WheelState
		{
			public float BaseStiffness;
			public float Applied;
			public bool Modified;
		}
	}
}
