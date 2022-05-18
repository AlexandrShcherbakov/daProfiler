using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Collections.ObjectModel;
using System.Net.Sockets;
using System.Threading;
using System.Net;
using System.IO;
using System.Windows.Threading;
using Profiler.Data;
using Frame = Profiler.Data.Frame;
using Microsoft.Win32;
using System.Xml;
using System.Net.Cache;
using System.Reflection;
using System.Diagnostics;
using System.Web;
using System.Net.NetworkInformation;
using System.ComponentModel;
using System.IO.Compression;
using System.Threading.Tasks;
using System.Security;
using Profiler.Controls;

namespace Profiler
{
	public delegate void ClearAllFramesHandler();

	/// <summary>
	/// Interaction logic for TimeLine.xaml
	/// </summary>
	public partial class TimeLine : UserControl
	{
		FrameCollection frames = new FrameCollection();
		Thread socketThread = null;

		Object criticalSection = new Object();

		public FrameCollection Frames
		{
			get
			{
				return frames;
			}
		}

		public TimeLine()
		{
			this.InitializeComponent();
			this.DataContext = frames;

			statusToError.Add(TracerStatus.TRACER_ERROR_ACCESS_DENIED, new KeyValuePair<string, string>("ETW can't start: launch your Game/VisualStudio/UE4Editor as administrator to collect context switches", "https://github.com/bombomby/optick/wiki/Event-Tracing-for-Windows"));
			statusToError.Add(TracerStatus.TRACER_ERROR_ALREADY_EXISTS, new KeyValuePair<string, string>("ETW session already started (Reboot should help)", "https://github.com/bombomby/optick/wiki/Event-Tracing-for-Windows"));
			statusToError.Add(TracerStatus.TRACER_FAILED, new KeyValuePair<string, string>("ETW session failed (Run your Game or Visual Studio as Administrator to get ETW data)", "https://github.com/bombomby/optick/wiki/Event-Tracing-for-Windows"));
			statusToError.Add(TracerStatus.TRACER_INVALID_PASSWORD, new KeyValuePair<string, string>("Tracing session failed: invalid root password. Run the game as a root or pass a valid password through daProfiler GUI", "https://github.com/bombomby/optick/wiki/Event-Tracing-for-Windows"));
			statusToError.Add(TracerStatus.TRACER_NOT_IMPLEMENTED, new KeyValuePair<string, string>("Tracing sessions are not supported yet on the selected platform! Stay tuned!", "https://github.com/bombomby/optick"));

			ProfilerClient.Get().ConnectionChanged += TimeLine_ConnectionChanged;

			socketThread = new Thread(RecieveMessage);
			socketThread.Start();
		}

		private void TimeLine_ConnectionChanged(IPAddress address, UInt16 port, ProfilerClient.State state, String message)
		{
			switch (state)
			{
				case ProfilerClient.State.Connecting:
					StatusText.Text = String.Format("Connecting {0}:{1} ...", address == null ? "null" : address.ToString(), port);
					StatusText.Visibility = System.Windows.Visibility.Visible;
					break;

				case ProfilerClient.State.Disconnected:
					RaiseEvent(new ShowWarningEventArgs("Connection Failed! " + message, String.Empty));
					StatusText.Visibility = System.Windows.Visibility.Collapsed;
					break;

				case ProfilerClient.State.Connected:
					StatusText.Text = String.Format("Connected to {0}:{1} ...", address.ToString(), port);
					StatusText.Visibility = Visibility.Collapsed;
					break;
			}
		}

		public bool LoadFile(string file)
		{
			if (File.Exists(file))
			{
				using (new WaitCursor())
				{
					if (System.IO.Path.GetExtension(file) == ".trace")
					{
						lock (frames)
							{ frames.Clear(); }						
						return OpenTrace<FTraceGroup>(file);
					}
					else if (System.IO.Path.GetExtension(file) == ".json")
					{
						lock (frames)
						{ frames.Clear(); }
						return OpenTrace<ChromeTracingGroup>(file);
					}
					else
					{
						using (Stream stream = Data.Capture.Open(file))
						{
							return Open(file, stream);
						}
					}
				}
			}
			return false;
		}

		private bool Open(String name, Stream stream)
		{
			DataResponse response = DataResponse.Create(stream);
			while (response != null)
			{
				if (!ApplyResponse(response))
					return false;

				response = DataResponse.Create(stream);
				if (response == null)
				{
					Stream stream2 = Data.Capture.ReOpen(stream);
					if (stream2 != null)
					{
						stream = stream2;
						response = DataResponse.Create(stream);
					}
				}
			}

			frames.UpdateName(name);
			frames.Flush();
			ScrollToEnd();

			return true;
		}

		private bool OpenTrace<T>(String path) where T : ITrace, new()
		{
			if (File.Exists(path))
			{
				using (Stream stream = File.OpenRead(path))
				{
					T trace = new T();
					trace.Init(path, stream);
					frames.AddGroup(trace.MainGroup);
					frames.Add(trace.MainFrame);
					FocusOnFrame(trace.MainFrame);
				}
				return true;
			}
			return false;
		}

		Dictionary<DataResponse.Type, int> testResponses = new Dictionary<DataResponse.Type, int>();

		private void SaveTestResponse(DataResponse response)
		{
			if (!testResponses.ContainsKey(response.ResponseType))
				testResponses.Add(response.ResponseType, 0);

			int count = testResponses[response.ResponseType]++;

			String data = response.SerializeToBase64();
			String path = response.ResponseType.ToString() + "_" + String.Format("{0:000}", count) + ".bin";
			File.WriteAllText(path, data);

		}

		public class ThreadDescription
		{
			public UInt32 ThreadID { get; set; }
			public String Name { get; set; }

			public override string ToString()
			{
				return String.Format("[{0}] {1}", ThreadID, Name);
			}
		}

		enum TracerStatus
		{
			TRACER_OK = 0,
			TRACER_ERROR_ALREADY_EXISTS = 1,
			TRACER_ERROR_ACCESS_DENIED = 2,
			TRACER_FAILED = 3,
			TRACER_INVALID_PASSWORD = 4,
			TRACER_NOT_IMPLEMENTED = 5,
		}

		Dictionary<TracerStatus, KeyValuePair<String, String>> statusToError = new Dictionary<TracerStatus, KeyValuePair<String, String>>();

		public static double frameMsToHeight = 1;
		public static double Square(double a) { return a * a; }
		int lastFramesCount = 0;
		public void RecalcHeight()
        {
			double height = frameList.ActualHeight;
			double avg = 0;
			lastFramesCount = frames.Count;
			foreach (Frame frame in frames)
				avg += frame.Duration;

			if (frames.Count > 0)
				avg /= frames.Count;
			if (frames.Count > 1)
			{
				double var = 0;
				foreach (Frame frame in frames)
					var += Square(frame.Duration - avg);
				var /= frames.Count - 1;
				double std = Math.Sqrt(var);
				double maxStd = Math.Min(1000 / 10, avg + 3 * std);//10 fps
				frameMsToHeight = 66 / maxStd;
				foreach (Frame frame in frames)
					if (frame is EventFrame)
						((EventFrame)frame).OnPropertyChanged("Duration");
			}
		}
		public void StopCaptureNow()
		{
			RaiseEvent(new StopCaptureEventArgs());
			StatusText.Visibility = System.Windows.Visibility.Collapsed;
			lock (frames)
			{
				frames.Flush();
				ScrollToEnd();
			}
		}

		public void CancelCapture()
		{
			RaiseEvent(new CancelConnectionEventArgs());
			StatusText.Visibility = System.Windows.Visibility.Collapsed;
			lock (frames)
			{
				frames.Flush();
				ScrollToEnd();
			}
		}
		private bool ApplyResponse(DataResponse response)
		{
			if (response.Version >= NetworkProtocol.NETWORK_PROTOCOL_MIN_VERSION)
			{
				//SaveTestResponse(response);

				switch (response.ResponseType)
				{
					case DataResponse.Type.ReportProgress:
						Int32 length = response.Reader.ReadInt32();
						StatusText.Text = new String(response.Reader.ReadChars(length));
						break;

					case DataResponse.Type.SettingsPack:
					    CaptureSettings settings = new CaptureSettings();
					    settings.Read(response.Reader);
						RaiseEvent(new UpdateSettingsEventArgs(settings));
						break;

					case DataResponse.Type.NullFrame:
						StopCaptureNow();
						break;

					case DataResponse.Type.Handshake:
						TracerStatus status = (TracerStatus)response.Reader.ReadUInt32();

						KeyValuePair<string, string> warning;
						if (statusToError.TryGetValue(status, out warning))
						{
							RaiseEvent(new ShowWarningEventArgs(warning.Key, warning.Value));
						}

						Platform.Connection connection = new Platform.Connection() {
							Address = response.Source.Address.ToString(),
							Port = response.Source.Port
						};
						Platform.Type target = Platform.Type.unknown;
						String targetName = Utils.ReadBinaryString(response.Reader);
						Enum.TryParse(targetName, true, out target);
						connection.Target = target;
						connection.Name = Utils.ReadBinaryString(response.Reader);
						RaiseEvent(new NewConnectionEventArgs(connection));

						break;
					case DataResponse.Type.UniqueName:
						String uniqueName = Utils.ReadVlqString(response.Reader);
						lock (frames)
						{
							if (frames.uniqueRunName != uniqueName)
							{
								frames.uniqueRunName = uniqueName;
								frames.Clear();
							}
						}
						break;
					

					default:
						lock (frames)
						{
							frames.Add(response);
							//ScrollToEnd();
						}
						break;
				}
			}
			else
			{
				RaiseEvent(new ShowWarningEventArgs("Invalid NETWORK_PROTOCOL_VERSION", String.Empty));
				return false;
			}
			return true;
		}

		private void ScrollToEnd()
		{
			if (frames.Count > 0)
			{
				RecalcHeight();
				frameList.SelectedItem = frames[frames.Count - 1];
				frameList.ScrollIntoView(frames[frames.Count - 1]);
			}
		}

		public void RecieveMessage()
		{
			uint processedResponses = 0, lastProcessedResponses = 0;
			while (true)
			{
				DataResponse response = ProfilerClient.Get().RecieveMessage();
				uint currentProcessed = processedResponses;

				if (response != null)
				{
					if (response.ResponseType != DataResponse.Type.Heartbeat)
					{
						Application.Current.Dispatcher.BeginInvoke(new Action(() => ApplyResponse(response)));
						processedResponses++;
					}
				}
				else
					Thread.Sleep(1000);
				if (currentProcessed == processedResponses && lastProcessedResponses != processedResponses)
				{
					lock (frames)
					{
						if (frames.Count != lastFramesCount)
							RecalcHeight();
					}
				}
			}
		}

		#region FocusFrame
		private void FocusOnFrame(Data.Frame frame)
		{
			FocusFrameEventArgs args = new FocusFrameEventArgs(GlobalEvents.FocusFrameEvent, frame);
			RaiseEvent(args);
		}

        public class ShowWarningEventArgs : RoutedEventArgs
		{
			public String Message { get; set; }
			public String URL { get; set; }

			public ShowWarningEventArgs(String message, String url) : base(ShowWarningEvent)
			{
				Message = message;
				URL = url;
			}
		}

		public class NewConnectionEventArgs : RoutedEventArgs
		{
			public Platform.Connection Connection { get; set; }

			public NewConnectionEventArgs(Platform.Connection connection) : base(NewConnectionEvent)
			{
				Connection = connection;
			}
		}

		public class UpdateSettingsEventArgs : RoutedEventArgs
		{
			public CaptureSettings Settings { get; set; }

			public UpdateSettingsEventArgs(CaptureSettings settings) : base(UpdateSettingsEvent)
			{
				Settings = settings;
			}
		}

		public class CancelConnectionEventArgs : RoutedEventArgs
		{
			public CancelConnectionEventArgs() : base(CancelConnectionEvent)
			{
			}
		}

		public class StopCaptureEventArgs : RoutedEventArgs
		{
			public StopCaptureEventArgs() : base(StopCaptureEvent)
			{
			}
		}

		public delegate void ShowWarningEventHandler(object sender, ShowWarningEventArgs e);
		public delegate void NewConnectionEventHandler(object sender, NewConnectionEventArgs e);
		public delegate void UpdateSettingsEventHandler(object sender, UpdateSettingsEventArgs e);
		public delegate void CancelConnectionEventHandler(object sender, CancelConnectionEventArgs e);
		public delegate void StopCaptureEventHandler(object sender, StopCaptureEventArgs e);

		public static readonly RoutedEvent ShowWarningEvent = EventManager.RegisterRoutedEvent("ShowWarning", RoutingStrategy.Bubble, typeof(ShowWarningEventArgs), typeof(TimeLine));
		public static readonly RoutedEvent NewConnectionEvent = EventManager.RegisterRoutedEvent("NewConnection", RoutingStrategy.Bubble, typeof(NewConnectionEventHandler), typeof(TimeLine));
		public static readonly RoutedEvent UpdateSettingsEvent = EventManager.RegisterRoutedEvent("UpdateSettings", RoutingStrategy.Bubble, typeof(UpdateSettingsEventHandler), typeof(TimeLine));
		public static readonly RoutedEvent CancelConnectionEvent = EventManager.RegisterRoutedEvent("CancelConnection", RoutingStrategy.Bubble, typeof(CancelConnectionEventHandler), typeof(TimeLine));
		public static readonly RoutedEvent StopCaptureEvent = EventManager.RegisterRoutedEvent("StopCapture", RoutingStrategy.Bubble, typeof(StopCaptureEventHandler), typeof(TimeLine));

		public event RoutedEventHandler FocusFrame
		{
			add { AddHandler(GlobalEvents.FocusFrameEvent, value); }
			remove { RemoveHandler(GlobalEvents.FocusFrameEvent, value); }
		}

		public event RoutedEventHandler ShowWarning
		{
			add { AddHandler(ShowWarningEvent, value); }
			remove { RemoveHandler(ShowWarningEvent, value); }
		}

		public event RoutedEventHandler NewConnection
		{
			add { AddHandler(NewConnectionEvent, value); }
			remove { RemoveHandler(NewConnectionEvent, value); }
		}

		public event RoutedEventHandler UpdateSettings
		{
			add { AddHandler(UpdateSettingsEvent, value); }
			remove { RemoveHandler(UpdateSettingsEvent, value); }
		}

		public event RoutedEventHandler CancelConnection
		{
			add { AddHandler(CancelConnectionEvent, value); }
			remove { RemoveHandler(CancelConnectionEvent, value); }
		}
		public event StopCaptureEventHandler StopCapture
		{
			add { AddHandler(StopCaptureEvent, value); }
			remove { RemoveHandler(StopCaptureEvent, value); }
		}
		#endregion

		private void frameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			if (frameList.SelectedItem is Data.Frame)
			{
				FocusOnFrame((Data.Frame)frameList.SelectedItem);
			}
		}

		public void ForEachResponse(Action<FrameGroup, DataResponse> action)
		{
			FrameGroup currentGroup = null;
			foreach (Frame frame in frames)
			{
				if (frame is EventFrame)
				{
					EventFrame eventFrame = frame as EventFrame;
					if (eventFrame.Group != currentGroup && currentGroup != null)
					{
						currentGroup.Responses.ForEach(response => action(currentGroup, response));
					}
					currentGroup = eventFrame.Group;
				}
				else if (frame is SamplingFrame)
				{
					if (currentGroup != null)
					{
						currentGroup.Responses.ForEach(response => action(currentGroup, response));
						currentGroup = null;
					}

					action(null, (frame as SamplingFrame).Response);
				}
			}

			currentGroup?.Responses.ForEach(response => action(currentGroup, response));
		}

		public void Save(Stream stream)
		{
			ForEachResponse((group, response) => response.Serialize(stream));
		}

		public String Save()
		{
			SaveFileDialog dlg = new SaveFileDialog();
			dlg.Filter = "daProfiler Performance Capture (*.dap)|*.dap";
			dlg.Title = "Where should I save profiler results?";

			if (dlg.ShowDialog() == true)
			{
				lock (frames)
				{
					using (Stream stream = Capture.Create(dlg.FileName))
						Save(stream);

					frames.UpdateName(dlg.FileName, true);
				}
				return dlg.FileName;
			}

			return null;
		}

		public void Close()
		{
			if (socketThread != null)
			{
				socketThread.Abort();
				socketThread = null;
			}
		}

		public void Clear()
		{
			lock (frames)
			{
				frames.Clear();
			}
		}

		//private void FrameFilterSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
		//{
		//	ICollectionView view = CollectionViewSource.GetDefaultView(frameList.ItemsSource);
		//	view.Filter = new Predicate<object>((item) => { return (item is Frame) ? (item as Frame).Duration >= FrameFilterSlider.Value : true; });
		//}

		public void StartCapture(IPAddress address, UInt16 port, CaptureSettings settings, SecureString password)
		{
            ProfilerClient.Get().IpAddress = address;
            ProfilerClient.Get().Port = port;

            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
			{
				StatusText.Text = "Capturing...";
				StatusText.Visibility = System.Windows.Visibility.Visible;
			}));

			Task.Run(() =>
			{
				ProfilerClient.Get().SendMessage(new SetSettingsMessage() {Settings = settings}, true);
				ProfilerClient.Get().SendMessage(new StartCaptureMessage(), true);
			});
		}

		public void GetCapture(IPAddress address, UInt16 port, SecureString password)
		{
            ProfilerClient.Get().IpAddress = address;
            ProfilerClient.Get().Port = port;

			Task.Run(() =>
			{
				ProfilerClient.Get().SendMessage(new GetCaptureMessage(), true);
			});
		}

		public void SendSettings(CaptureSettings settings)
		{
			Task.Run(() => { ProfilerClient.Get().SendMessage(new SetSettingsMessage() {Settings = settings}, false); });
		}

		public void Connect(IPAddress address, UInt16 port)
		{
            ProfilerClient.Get().IpAddress = address;
            ProfilerClient.Get().Port = port;

            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
			{
				StatusText.Text = "Connecting...";
				StatusText.Visibility = System.Windows.Visibility.Visible;
			}));

			Task.Run(() => { ProfilerClient.Get().SendMessage(new ConnectMessage() , true); });
		}
		public void Disconnect()
		{
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
			{
				StatusText.Text = "Disconnecting...";
				StatusText.Visibility = System.Windows.Visibility.Visible;
			}));

			Task.Run(() => { ProfilerClient.Get().SendMessage(new DisconnectMessage(), false); });
		}
	}

	public class FrameHeightConverter : IValueConverter
	{
		public static double Convert(double value)
		{
			return value * TimeLine.frameMsToHeight;
		}

		public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
		{
			return Convert((double)value);
		}

		public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
		{
			return null;
		}
	}


	public class WaitCursor : IDisposable
	{
		private Cursor _previousCursor;

		public WaitCursor()
		{
			_previousCursor = Mouse.OverrideCursor;

			Mouse.OverrideCursor = Cursors.Wait;
		}

		public void Dispose()
		{
			Mouse.OverrideCursor = _previousCursor;
		}
	}
}
