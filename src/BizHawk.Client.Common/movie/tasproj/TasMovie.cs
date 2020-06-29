﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;

using BizHawk.Emulation.Common;

namespace BizHawk.Client.Common
{
	internal sealed partial class TasMovie : Bk2Movie, ITasMovie
	{
		public new const string Extension = "tasproj";
		private IInputPollable _inputPollable;

		/// <exception cref="InvalidOperationException">loaded core does not implement <see cref="IStatable"/></exception>
		internal TasMovie(IMovieSession session, string path) : base(session, path)
		{
			Branches = new TasBranchCollection(this);
			ChangeLog = new TasMovieChangeLog(this);
			Header[HeaderKeys.MovieVersion] = "BizHawk v2.0 Tasproj v1.0";
			Markers = new TasMovieMarkerList(this);
			Markers.CollectionChanged += Markers_CollectionChanged;
			Markers.Add(0, "Power on");
		}

		public override void Attach(IEmulator emulator)
		{
			if (!emulator.HasSavestates())
			{
				throw new InvalidOperationException($"A core must be able to provide an {nameof(IStatable)} service");
			}

			if (!emulator.CanPollInput())
			{
				throw new InvalidOperationException($"A core must be able to provide an {nameof(IInputPollable)} service");
			}

			_inputPollable = emulator.AsInputPollable();
			// TODO: Where's the frame 0 state, do I have to fish it out of somewhere?
			TasStateManager = new TasStateManager(this, emulator, Session.Settings.DefaultTasStateManagerSettings, null);

			base.Attach(emulator);

			foreach (var button in emulator.ControllerDefinition.BoolButtons)
			{
				_mnemonicCache[button] = Bk2MnemonicLookup.Lookup(button, emulator.SystemId);
			}
		}

		private readonly Dictionary<string, char> _mnemonicCache = new Dictionary<string, char>();

		public override bool StartsFromSavestate
		{
			get => base.StartsFromSavestate;
			set
			{
				Markers.Add(0, value ? "Savestate" : "Power on");
				base.StartsFromSavestate = value;
			}
		}

		public IStringLog VerificationLog { get; } = StringLogUtil.MakeStringLog(); // For movies that do not begin with power-on, this is the input required to get into the initial state
		public ITasBranchCollection Branches { get; }
		public ITasSession TasSession { get; private set; } = new TasSession();

		public int LastEditedFrame { get; private set; } = -1;
		public bool LastPositionStable { get; set; } = true;
		public TasMovieMarkerList Markers { get; private set; }
		public bool BindMarkersToInput { get; set; }

		public TasLagLog LagLog { get; } = new TasLagLog();

		public override string PreferredExtension => Extension;
		public IStateManager TasStateManager { get; private set; }

		public Action<int> GreenzoneInvalidated { get; set; }

		public ITasMovieRecord this[int index]
		{
			get
			{
				var lagIndex = index + 1;
				var lagged = LagLog[lagIndex];
				if (lagged == null && Emulator.Frame == lagIndex)
				{
					lagged = _inputPollable.IsLagFrame;
				}

				return new TasMovieRecord
				{
					HasState = TasStateManager.HasState(index),
					LogEntry = GetInputLogEntry(index),
					Lagged = lagged,
					WasLagged = LagLog.History(lagIndex)
				};
			}
		}

		public override void StartNewRecording()
		{
			ClearTasprojExtras();
			Markers.Add(0, StartsFromSavestate ? "Savestate" : "Power on");
			ChangeLog = new TasMovieChangeLog(this);

			base.StartNewRecording();
		}

		// Removes lag log and greenzone after this frame
		private void InvalidateAfter(int frame)
		{
			var anyInvalidated = LagLog.RemoveFrom(frame);
			TasStateManager.Invalidate(frame + 1);
			GreenzoneInvalidated(frame + 1);
			if (anyInvalidated)
			{
				Changes = true;
			}
			LastEditedFrame = frame;

			if (anyInvalidated && IsCountingRerecords)
			{
				Rerecords++;
			}
		}


		private (int Frame, IMovieController Controller) _displayCache = (-1, new Bk2Controller("", NullController.Instance.Definition));

		/// <summary>
		/// Returns the mnemonic value for boolean buttons, and actual value for axes,
		/// for a given frame and button.
		/// </summary>
		public string DisplayValue(int frame, string buttonName)
		{
			if (_displayCache.Frame != frame)
			{
				_displayCache = (frame, GetInputState(frame));
			}
			
			return CreateDisplayValueForButton(_displayCache.Controller, buttonName);
		}

		private string CreateDisplayValueForButton(IController adapter, string buttonName)
		{
			if (adapter.Definition.BoolButtons.Contains(buttonName))
			{
				return adapter.IsPressed(buttonName)
					? _mnemonicCache[buttonName].ToString()
					: "";
			}

			if (adapter.Definition.Axes.ContainsKey(buttonName))
			{
				return adapter.AxisValue(buttonName).ToString();
			}

			return "!";
		}

		public void GreenzoneCurrentFrame()
		{
			// todo: this isn't working quite right when autorestore is off and we're editing while seeking
			// but accounting for that requires access to Mainform.IsSeeking
			if (Emulator.Frame > LastEditedFrame)
			{
				// emulated a new frame, current editing segment may change now. taseditor logic
				LastPositionStable = false;
			}

			LagLog[Emulator.Frame] = _inputPollable.IsLagFrame;

			if (!TasStateManager.HasState(Emulator.Frame))
			{
				TasStateManager.Capture(Emulator.Frame, Emulator.AsStatable(), Emulator.Frame == LastEditedFrame - 1);
			}
		}

		
		public void CopyVerificationLog(IEnumerable<string> log)
		{
			foreach (string entry in log)
			{
				VerificationLog.Add(entry);
			}
		}

		// TODO: this is 99% copy pasting of bad code
		public override bool ExtractInputLog(TextReader reader, out string errorMessage)
		{
			errorMessage = "";
			int? stateFrame = null;

			var newLog = new List<string>();
			int? timelineBranchFrame = null;

			// We are in record mode so replace the movie log with the one from the savestate
			if (Session.Settings.EnableBackupMovies && MakeBackup && Log.Count != 0)
			{
				SaveBackup();
				MakeBackup = false;
			}

			int counter = 0;
			string line;
			while ((line = reader.ReadLine()) != null)
			{
				if (line.StartsWith("|"))
				{
					newLog.Add(line);
					if (!timelineBranchFrame.HasValue && counter < Log.Count && line != Log[counter])
					{
						timelineBranchFrame = counter;
					}

					counter++;
				}
				else if (line.StartsWith("Frame "))
				{
					var split = line.Split(' ');
					try
					{
						stateFrame = int.Parse(split[1]);
					}
					catch
					{
						errorMessage = "Savestate Frame number failed to parse";
						return false;
					}
				}
				else if (line.StartsWith("LogKey:"))
				{
					LogKey = line.Replace("LogKey:", "");
				}
			}

			Log.Clear();
			Log.AddRange(newLog);

			if (!stateFrame.HasValue)
			{
				errorMessage = "Savestate Frame number failed to parse";
			}

			var stateFrameValue = stateFrame ?? 0;

			if (stateFrameValue > 0 && stateFrameValue < Log.Count)
			{
				if (!Session.Settings.VBAStyleMovieLoadState)
				{
					Truncate(stateFrameValue);
				}
			}
			else if (stateFrameValue > Log.Count) // Post movie savestate
			{
				if (!Session.Settings.VBAStyleMovieLoadState)
				{
					Truncate(Log.Count);
				}

				Mode = MovieMode.Finished;
			}

			if (IsCountingRerecords)
			{
				Rerecords++;
			}

			if (timelineBranchFrame.HasValue)
			{
				LagLog.RemoveFrom(timelineBranchFrame.Value);
				TasStateManager.Invalidate(timelineBranchFrame.Value);
				GreenzoneInvalidated(timelineBranchFrame.Value);
			}

			return true;
		}

		public void LoadBranch(TasBranch branch)
		{
			int? divergentPoint = Log.DivergentPoint(branch.InputLog);

			Log?.Dispose();
			Log = branch.InputLog.Clone();

			InvalidateAfter(divergentPoint ?? branch.InputLog.Count);

			if (BindMarkersToInput) // pretty critical not to erase them
			{
				Markers = branch.Markers;
			}

			Changes = true;
		}

		public event PropertyChangedEventHandler PropertyChanged;

		private bool _changes;
		public override bool Changes
		{
			get => _changes;
			protected set
			{
				if (_changes != value)
				{
					_changes = value;
					OnPropertyChanged("Changes");
				}
			}
		}

		// This event is Raised only when Changes is TOGGLED.
		private void OnPropertyChanged(string propertyName)
		{
			// Raising the event when FirstName or LastName property value changed
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		}

		private void Markers_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
		{
			Changes = true;
		}

		public void ClearChanges() => Changes = false;
		public void FlagChanges() => Changes = true;
	}
}
