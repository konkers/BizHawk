using System;
using System.Collections.Generic;
using System.IO;
using BizHawk.Emulation.Common;

namespace BizHawk.Client.Common
{
	// todo: maybe interface this?
	public class ZwinderStateManagerSettingsWIP
	{
		/// <summary>
		/// Buffer settings when navigating near now
		/// </summary>
		public IRewindSettings Current { get; set; }
		/// <summary>
		/// Buffer settings when navigating directly before the Current buffer
		/// </summary>
		/// <value></value>
		public IRewindSettings Recent { get; set; }
		/// <summary>
		/// How often to maintain states when outside of Current and Recent intervals
		/// </summary>
		/// <value></value>
		public int AncientStateInterval { get; set; }
	}
	public class ZwinderStateManager : IStateManager
	{
		private static readonly byte[] NonState = new byte[0];

		private readonly byte[] _originalState;
		private readonly ZwinderBuffer _current;
		private readonly ZwinderBuffer _recent;
		private readonly List<KeyValuePair<int, byte[]>> _ancient = new List<KeyValuePair<int, byte[]>>();
		private readonly int _ancientInterval;

		public ZwinderStateManager(ZwinderStateManagerSettingsWIP fixme, byte[] frameZeroState)
		{
			_originalState = (byte[])frameZeroState.Clone();
			_current = new ZwinderBuffer(fixme.Current);
			_recent = new ZwinderBuffer(fixme.Recent);
			_ancientInterval = fixme.AncientStateInterval;
		}
		
		public byte[] this[int frame] => throw new NotImplementedException();

		public TasStateManagerSettings Settings { get; set; }
		public int Count => _current.Count + _recent.Count + _ancient.Count + 1;

		public int Last
		{
			get
			{
				if (_current.Count > 0)
					return _current.GetState(_current.Count - 1).Frame;
				if (_recent.Count > 0)
					return _recent.GetState(_current.Count - 1).Frame;
				if (_ancient.Count > 0)
					return _ancient[_ancient.Count - 1].Key;
				return 0;
			}
		}

		public bool Any() => true;

		public void Capture(int frame, IBinaryStateable source, bool force = false)
		{
			_current.Capture(frame,
				s => source.SaveStateBinary(new BinaryWriter(s)),
				index =>
				{
					var state = _current.GetState(index);
					_recent.Capture(state.Frame,
						s => state.GetReadStream().CopyTo(s),
						index2 => 
						{
							var state2 = _recent.GetState(index2);
							var from = _ancient.Count > 0 ? _ancient[_ancient.Count - 1].Key : 0;
							if (state2.Frame - from >= _ancientInterval) 
							{
								var ms = new MemoryStream();
								state2.GetReadStream().CopyTo(ms);
								_ancient.Add(new KeyValuePair<int, byte[]>(state2.Frame, ms.ToArray()));
							}
						});
				},
				force);
		}

		public void Clear()
		{
			_current.InvalidateEnd(0);
			_recent.InvalidateEnd(0);
			_ancient.Clear();
		}

		public KeyValuePair<int, Stream> GetStateClosestToFrame(int frame)
		{
			if (frame <= 0)
				throw new ArgumentOutOfRangeException(nameof(frame));

			for (var i = _current.Count - 1; i >= 0; i--)
			{
				var s = _current.GetState(i);
				if (s.Frame < frame)
					return new KeyValuePair<int, Stream>(s.Frame, s.GetReadStream());
			}
			for (var i = _recent.Count - 1; i >= 0; i--)
			{
				var s = _recent.GetState(i);
				if (s.Frame < frame)
					return new KeyValuePair<int, Stream>(s.Frame, s.GetReadStream());
			}
			for (var i = _ancient.Count - 1; i >= 0; i--)
			{
				if (_ancient[i].Key < frame)
					return new KeyValuePair<int, Stream>(_ancient[i].Key, new MemoryStream(_ancient[i].Value, false));
			}
			return new KeyValuePair<int, Stream>(0, new MemoryStream(_originalState, false));
		}

		public bool HasState(int frame)
		{
			if (frame == 0)
			{
				return true;
			}
			for (var i = _current.Count - 1; i >= 0; i--)
			{
				if (_current.GetState(i).Frame == frame)
					return true;
			}
			for (var i = _recent.Count - 1; i >= 0; i--)
			{
				if (_recent.GetState(i).Frame == frame)
					return true;
			}
			for (var i = _ancient.Count - 1; i >= 0; i--)
			{
				if (_ancient[i].Key == frame)
					return true;
			}
			return false;
		}

		public bool Invalidate(int frame)
		{
			if (frame <= 0)
				throw new ArgumentOutOfRangeException(nameof(frame));
			for (var i = 0; i < _ancient.Count; i++)
			{
				if (_ancient[i].Key >= frame)
				{
					_ancient.RemoveRange(i, _ancient.Count - i);
					_recent.InvalidateEnd(0);
					_current.InvalidateEnd(0);
					return true;
				}
			}
			for (var i = 0; i < _recent.Count; i++)
			{
				if (_recent.GetState(i).Frame >= frame)
				{
					_recent.InvalidateEnd(i);
					_current.InvalidateEnd(0);
					return true;
				}
			}
			for (var i = 0; i < _current.Count; i++)
			{
				if (_current.GetState(i).Frame >= frame)
				{
					_current.InvalidateEnd(i);
					return true;
				}
			}
			return false;
		}

		public void UpdateStateFrequency()
		{
			throw new NotImplementedException();
		}

		public void LoadStateBinary(BinaryReader br)
		{
			_current.LoadStateBinary(br);
			_recent.LoadStateBinary(br);
			_ancient.Clear();
			var count = br.ReadInt32();
			for (var i = 0; i < count; i++)
			{
				var key = br.ReadInt32();
				var length = br.ReadInt32();
				var data = br.ReadBytes(length);
				_ancient.Add(new KeyValuePair<int, byte[]>(key, data));
			}
			if (_originalState.Length != br.ReadInt32())
			{
				throw new InvalidOperationException("Invalid data; rewinder cannot load into a different emulator");
			}
			br.Read(_originalState, 0, _originalState.Length);
		}

		public void SaveStateBinary(BinaryWriter bw)
		{
			_current.SaveStateBinary(bw);
			_recent.SaveStateBinary(bw);
			bw.Write(_ancient.Count);
			foreach (var s in _ancient)
			{
				bw.Write(s.Key);
				bw.Write(s.Value.Length);
				bw.Write(s.Value);
			}
			bw.Write(_originalState.Length);
			bw.Write(_originalState);
		}
	}
}
