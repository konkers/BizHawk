﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using BizHawk.Common.NumberExtensions;
using BizHawk.Emulation.Common;

namespace BizHawk.Client.Common
{
	/// <summary>
	/// Captures savestates and manages the logic of adding, retrieving, 
	/// invalidating/clearing of states.  Also does memory management and limiting of states
	/// </summary>
	public class TasStateManager : IStateManager
	{
		private const int MinFrequency = 1;
		private const int MaxFrequency = 16;

		private IStatable _core;
		private IEmulator _emulator;

		private StateManagerDecay _decay;
		private readonly ITasMovie _movie;

		private SortedList<int, byte[]> _states  = new SortedList<int, byte[]>();
		private double _expectedStateSizeInMb;

		private ulong _used;
		private int _stateFrequency;
		
		private int MaxStates => (int)(Settings.CapacityMb / _expectedStateSizeInMb + 1);
		private int FileStateGap => 1 << Settings.FileStateGap;

		/// <exception cref="InvalidOperationException">loaded core expects savestate size of <c>0 B</c></exception>
		public TasStateManager(ITasMovie movie, IEmulator emulator, TasStateManagerSettings settings, byte[] frameZeroState)
		{
			_movie = movie;
			Settings = new TasStateManagerSettings(settings);

			SetState(0, frameZeroState);
			if (!emulator.HasSavestates())
			{
				throw new InvalidOperationException($"A core must be able to provide an {nameof(IStatable)} service");
			}

			_emulator = emulator;
			_core = emulator.AsStatable();

			_decay = new StateManagerDecay(_movie, this);

			_expectedStateSizeInMb = _core.CloneSavestate().Length / (double)(1024 * 1024);
			if (_expectedStateSizeInMb.HawkFloatEquality(0))
			{
				throw new InvalidOperationException("Savestate size can not be zero!");
			}

			_states = new SortedList<int, byte[]>(MaxStates);

			UpdateStateFrequency();
		}

		public TasStateManagerSettings Settings { get; set; }

		public byte[] this[int frame]
		{
			get
			{
				if (frame == 0)
				{
					return InitialState;
				}

				if (_states.ContainsKey(frame))
				{
					return _states[frame];
				}

				return new byte[0];
			}
		}

		public int Count => _states.Count;

		public int Last => _states.Count > 0
			? _states.Last().Key
			: 0;

		private byte[] InitialState =>
			_movie.StartsFromSavestate
				? _movie.BinarySavestate
				: _states[0];

		public bool Any()
		{
			if (_movie.StartsFromSavestate)
			{
				return _states.Count > 0;
			}

			return _states.Count > 1;
		}

		public void UpdateStateFrequency()
		{
			_stateFrequency = ((int)_expectedStateSizeInMb / Settings.MemStateGapDivider / 1024)
				.Clamp(MinFrequency, MaxFrequency);

			_decay.UpdateSettings(MaxStates, _stateFrequency, 4);
			LimitStateCount();
		}

		public void Capture(int frame, IBinaryStateable source, bool force = false)
		{
			bool shouldCapture;

			if (_movie.StartsFromSavestate && frame == 0) // Never capture frame 0 on savestate anchored movies since we have it anyway
			{
				shouldCapture = false;
			}
			else if (force)
			{
				shouldCapture = true;
			}
			else if (frame == 0) // For now, long term, TasMovie should have a .StartState property, and a .tasproj file for the start state in non-savestate anchored movies
			{
				shouldCapture = true;
			}
			else if (IsMarkerState(frame))
			{
				shouldCapture = true; // Markers should always get priority
			}
			else
			{
				shouldCapture = frame % _stateFrequency == 0;
			}

			if (shouldCapture)
			{
				var ms = new MemoryStream();
				source.SaveStateBinary(new BinaryWriter(ms));
				SetState(frame, ms.ToArray(), skipRemoval: false);
			}
		}

		public void Clear()
		{
			if (_states.Any())
			{
				// For power-on movies, we can't lose frame 0;
				byte[] power = null;
				if (!_movie.StartsFromSavestate)
				{
					power = _states[0];
				}

				_states.Clear();

				if (power != null)
				{
					SetState(0, power);
					_used = (ulong)power.Length;
				}

				_movie.FlagChanges();
			}
		}

		public bool HasState(int frame)
		{
			if (_movie.StartsFromSavestate && frame == 0)
			{
				return true;
			}

			return _states.ContainsKey(frame);
		}

		/// <returns>true iff any frames were invalidated</returns>
		public bool Invalidate(int frame)
		{
			if (!Any()) return false;
			if (frame == 0) frame = 1; // Never invalidate frame 0
			var statesToRemove = _states.Where(s => s.Key >= frame).ToList();
			foreach (var state in statesToRemove) Remove(state.Key);
			return statesToRemove.Count != 0;
		}

		public bool Remove(int frame)
		{
			int index = _states.IndexOfKey(frame);

			if (frame < 1 || index < 1)
			{
				return false;
			}

			var state = _states[frame];

			_used -= (ulong)state.Length;

			_states.RemoveAt(index);

			return true;
		}

		// Map:
		// 4 bytes - total savestate count
		// [Foreach state]
		// 4 bytes - frame
		// 4 bytes - length of savestate
		// 0 - n savestate
		public void SaveStateBinary(BinaryWriter bw)
		{
			List<int> noSave = ExcludeStates();
			bw.Write(_states.Count - noSave.Count);

			for (int i = 0; i < _states.Count; i++)
			{
				if (noSave.Contains(i))
				{
					continue;
				}
				
				bw.Write(_states.Keys[i]);
				bw.Write(_states.Values[i].Length);
				bw.Write(_states.Values[i]);
			}
		}

		public void LoadStateBinary(BinaryReader br)
		{
			_states.Clear();

			try
			{
				int nstates = br.ReadInt32();

				for (int i = 0; i < nstates; i++)
				{
					int frame = br.ReadInt32();
					int len = br.ReadInt32();
					byte[] data = br.ReadBytes(len);

					// whether we should allow state removal check here is an interesting question
					// nothing was edited yet, so it might make sense to show the project untouched first
					SetState(frame, data);
				}
			}
			catch (EndOfStreamException)
			{
			}
		}

		public KeyValuePair<int, Stream> GetStateClosestToFrame(int frame)
		{
			var s = _states.LastOrDefault(state => state.Key < frame);
			if (s.Key > 0)
			{
				return new KeyValuePair<int, Stream>(s.Key, new MemoryStream(s.Value, false));
			}

			return new KeyValuePair<int, Stream>(0, new MemoryStream(InitialState, false));
		}

		public int GetStateIndexByFrame(int frame)
		{
			return _states.IndexOfKey(GetStateClosestToFrame(frame).Key);
		}

		public int GetStateFrameByIndex(int index)
		{
			return _states.Keys[index];
		}

		private bool IsMarkerState(int frame)
		{
			return _movie.Markers.IsMarker(frame + 1);
		}

		private void SetState(int frame, byte[] state, bool skipRemoval = true)
		{
			if (!skipRemoval) // skipRemoval: false only when capturing new states
			{
				LimitStateCount(); // Remove before adding so this state won't be removed.
			}

			if (_states.ContainsKey(frame))
			{
				_states[frame] = state;
			}
			else
			{
				_used += (ulong)state.Length;
				_states.Add(frame, state);
			}
		}

		// Deletes states to follow the state storage size limits.
		// Used after changing the settings too.
		private void LimitStateCount()
		{
			if (Count + 1 > MaxStates)
			{
				_decay.Trigger(_emulator.Frame, Count + 1 - MaxStates);
			}
		}

		private List<int> ExcludeStates()
		{
			List<int> ret = new List<int>();
			ulong saveUsed = _used;

			// respect state gap no matter how small the resulting size will be
			// still leave marker states
			for (int i = 1; i < _states.Count; i++)
			{
				int frame = GetStateFrameByIndex(i);

				if (IsMarkerState(frame) || frame % FileStateGap < _stateFrequency)
				{
					continue;
				}

				ret.Add(i);

				saveUsed -= (ulong)_states.Values[i].Length;
			}

			// if the size is still too big, exclude states form the beginning
			// still leave marker states
			int index = 0;
			while (saveUsed > (ulong)Settings.DiskSaveCapacityMb * 1024 * 1024)
			{
				do
				{
					if (++index >= _states.Count)
					{
						break;
					}
				}
				while (IsMarkerState(GetStateFrameByIndex(index)));

				if (index >= _states.Count)
				{
					break;
				}

				ret.Add(index);
				saveUsed -= (ulong)_states.Values[index].Length;
			}

			// if there are enough markers to still be over the limit, remove marker frames
			index = 0;
			while (saveUsed > (ulong)Settings.DiskSaveCapacityMb * 1024 * 1024)
			{
				if (!ret.Contains(++index))
				{
					ret.Add(index);
				}

				saveUsed -= (ulong)_states.Values[index].Length;
			}

			return ret;
		}
	}
}
