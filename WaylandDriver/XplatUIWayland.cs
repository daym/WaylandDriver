using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using WaylandDriver.Wayland;

namespace System.Windows.Forms {
	internal sealed class XplatUIWayland : XplatUIDriver {
		sealed class WaylandWindow {
			public Hwnd Hwnd;
			public string Text;
			public FormWindowState State;
			public Bitmap BackBuffer;
			public int BufferScale = 1;
			public uint SurfaceId;
			public uint SubsurfaceId;
			public uint XdgSurfaceId;
			public uint XdgToplevelId;
			public bool XdgConfigured;
			public readonly List<WaylandShmBuffer> Buffers = new List<WaylandShmBuffer> ();
			public readonly HashSet<uint> EnteredOutputs = new HashSet<uint> ();

			public void EnsureBackBuffer ()
			{
				Rectangle client = Hwnd.ClientRect;
				int width = Math.Max (1, client.Width);
				int height = Math.Max (1, client.Height);

				if (BackBuffer != null && BackBuffer.Width == width && BackBuffer.Height == height)
					return;

				if (BackBuffer != null)
					BackBuffer.Dispose ();

				BackBuffer = new Bitmap (width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
			}

			public void Dispose ()
			{
				if (BackBuffer != null)
					BackBuffer.Dispose ();
				BackBuffer = null;
			}
		}

		sealed class WaylandOutput {
			public uint ObjectId;
			public int Scale = 1;
		}

		readonly object queueLock = new object ();
		readonly Queue messageQueue = new Queue ();
		readonly Dictionary<IntPtr, WaylandWindow> windows = new Dictionary<IntPtr, WaylandWindow> ();
		readonly Dictionary<uint, WaylandWindow> waylandObjects = new Dictionary<uint, WaylandWindow> ();
		readonly Dictionary<uint, WaylandShmBuffer> waylandBuffers = new Dictionary<uint, WaylandShmBuffer> ();
		readonly Dictionary<uint, WaylandOutput> waylandOutputs = new Dictionary<uint, WaylandOutput> ();
		readonly List<IntPtr> zOrder = new List<IntPtr> ();
		readonly List<Timer> timers = new List<Timer> ();
		readonly Bitmap fallbackBitmap = new Bitmap (1, 1, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

		WaylandConnection connection;
		WaylandRegistry registry;
		uint compositorId;
		uint subcompositorId;
		uint shmId;
		uint xdgWmBaseId;
		int nextHandle = 0x4000;
		IntPtr activeWindow = IntPtr.Zero;
		IntPtr focusWindow = IntPtr.Zero;
		IntPtr grabWindow = IntPtr.Zero;
		IntPtr overrideCursor = IntPtr.Zero;
		bool themesEnabled;
		bool quitPosted;

		internal override event EventHandler Idle;

		internal override int CaptionHeight {
			get { return 22; }
		}

		internal override Size CursorSize {
			get { return new Size (32, 32); }
		}

		internal override bool DragFullWindows {
			get { return true; }
		}

		internal override Size DragSize {
			get { return new Size (4, 4); }
		}

		internal override Size FrameBorderSize {
			get { return new Size (4, 4); }
		}

		internal override Size IconSize {
			get { return new Size (32, 32); }
		}

		internal override Size MaxWindowTrackSize {
			get { return new Size (VirtualScreen.Width, VirtualScreen.Height); }
		}

		internal override bool MenuAccessKeysUnderlined {
			get { return false; }
		}

		internal override Size MinimizedWindowSpacingSize {
			get { return new Size (160, 24); }
		}

		internal override Size MinimumWindowSize {
			get { return new Size (1, 1); }
		}

		internal override Size SmallIconSize {
			get { return new Size (16, 16); }
		}

		internal override int MouseButtonCount {
			get { return 3; }
		}

		internal override bool MouseButtonsSwapped {
			get { return false; }
		}

		internal override bool MouseWheelPresent {
			get { return true; }
		}

		internal override Rectangle VirtualScreen {
			get { return new Rectangle (0, 0, 1024, 768); }
		}

		internal override Rectangle WorkingArea {
			get { return VirtualScreen; }
		}

		internal override Screen [] AllScreens {
			get { return new [] { new Screen (true, "Wayland Display", VirtualScreen, WorkingArea) }; }
		}

		internal override bool ThemesEnabled {
			get { return themesEnabled; }
		}

		internal override bool DropTarget {
			get { return false; }
			set { }
		}

		internal override int KeyboardSpeed {
			get { return 31; }
		}

		internal override int KeyboardDelay {
			get { return 1; }
		}

		internal override IntPtr InitializeDriver ()
		{
			connection = WaylandConnection.ConnectFromEnvironment ();
			registry = connection.GetRegistryRoundtrip ();
			compositorId = connection.Bind (registry, "wl_compositor", 4);
			subcompositorId = connection.Bind (registry, "wl_subcompositor", 1);
			shmId = connection.Bind (registry, "wl_shm", 1);
			xdgWmBaseId = connection.Bind (registry, "xdg_wm_base", 3);
			BindOutputs ();

			if (compositorId == 0)
				throw new InvalidOperationException ("The Wayland compositor did not advertise wl_compositor.");
			if (subcompositorId == 0)
				throw new InvalidOperationException ("The Wayland compositor did not advertise wl_subcompositor.");
			if (shmId == 0)
				throw new InvalidOperationException ("The Wayland compositor did not advertise wl_shm.");
			if (xdgWmBaseId == 0)
				throw new InvalidOperationException ("The Wayland compositor did not advertise xdg_wm_base.");

			return (IntPtr) 1;
		}

		internal override void ShutdownDriver (IntPtr token)
		{
			foreach (WaylandWindow window in windows.Values)
				window.Dispose ();
			foreach (WaylandShmBuffer buffer in waylandBuffers.Values)
				buffer.Dispose ();
			fallbackBitmap.Dispose ();
			windows.Clear ();
			waylandObjects.Clear ();
			waylandBuffers.Clear ();
			waylandOutputs.Clear ();

			if (connection != null)
				connection.Dispose ();
			connection = null;
		}

		internal override void AudibleAlert (AlertType alert)
		{
			System.Media.SystemSounds.Beep.Play ();
		}

		internal override void BeginMoveResize (IntPtr handle)
		{
		}

		internal override void EnableThemes ()
		{
			themesEnabled = true;
		}

		internal override void GetDisplaySize (out Size size)
		{
			size = VirtualScreen.Size;
		}

		internal override IntPtr CreateWindow (CreateParams cp)
		{
			Hwnd hwnd = new Hwnd ();
			Hwnd parent = cp.Parent == IntPtr.Zero ? null : Hwnd.ObjectFromHandle (cp.Parent);
			int width = Math.Max (1, cp.Width);
			int height = Math.Max (1, cp.Height);
			int x = cp.X;
			int y = cp.Y;

			if (cp.control is Form) {
				Point next = Hwnd.GetNextStackedFormLocation (cp);
				x = next.X;
				y = next.Y;
			}

			hwnd.X = x;
			hwnd.Y = y;
			hwnd.Width = width;
			hwnd.Height = height;
			hwnd.Parent = parent;
			hwnd.initial_style = cp.WindowStyle;
			hwnd.initial_ex_style = cp.WindowExStyle;
			hwnd.ClientRect = hwnd.GetClientRectangle (width, height);
			hwnd.visible = false;
			hwnd.enabled = !StyleSet (cp.Style, WindowStyles.WS_DISABLED);
			SetHwndStyles (hwnd, cp);

			IntPtr handle = AllocateSyntheticHandle ();
			hwnd.WholeWindow = handle;
			hwnd.ClientWindow = handle;

			WaylandWindow window = new WaylandWindow ();
			window.Hwnd = hwnd;
			window.Text = cp.Caption ?? String.Empty;
			window.State = FormWindowState.Normal;
			windows [handle] = window;
			zOrder.Add (handle);

			SendMessage (handle, Msg.WM_CREATE, (IntPtr) 1, IntPtr.Zero);
			SendParentNotify (handle, Msg.WM_CREATE, Int32.MaxValue, Int32.MaxValue);

			if (StyleSet (cp.Style, WindowStyles.WS_VISIBLE))
				SetVisible (handle, true, true);

			if (StyleSet (cp.Style, WindowStyles.WS_MINIMIZE))
				window.State = FormWindowState.Minimized;
			else if (StyleSet (cp.Style, WindowStyles.WS_MAXIMIZE))
				window.State = FormWindowState.Maximized;

			return handle;
		}

		internal override IntPtr CreateWindow (IntPtr Parent, int X, int Y, int Width, int Height)
		{
			CreateParams cp = new CreateParams ();
			cp.Caption = String.Empty;
			cp.X = X;
			cp.Y = Y;
			cp.Width = Width;
			cp.Height = Height;
			cp.ClassName = XplatUI.GetDefaultClassName (GetType ());
			cp.Parent = Parent;
			return CreateWindow (cp);
		}

		internal override void DestroyWindow (IntPtr handle)
		{
			WaylandWindow window;
			if (!windows.TryGetValue (handle, out window))
				return;

			DestroyChildNativeWindows (window);
			DestroyNativeWindow (window);
			SendMessage (handle, Msg.WM_DESTROY, IntPtr.Zero, IntPtr.Zero);
			SendParentNotify (handle, Msg.WM_DESTROY, Int32.MaxValue, Int32.MaxValue);

			window.Hwnd.Dispose ();
			window.Dispose ();
			windows.Remove (handle);
			zOrder.Remove (handle);
			if (activeWindow == handle)
				activeWindow = IntPtr.Zero;
			if (focusWindow == handle)
				focusWindow = IntPtr.Zero;
		}

		internal override FormWindowState GetWindowState (IntPtr handle)
		{
			WaylandWindow window;
			return windows.TryGetValue (handle, out window) ? window.State : FormWindowState.Normal;
		}

		internal override void SetWindowState (IntPtr handle, FormWindowState state)
		{
			WaylandWindow window;
			if (!windows.TryGetValue (handle, out window))
				return;

			window.State = state;
			if (window.XdgToplevelId == 0)
				return;

			if (state == FormWindowState.Maximized) {
				connection.SendRequest (window.XdgToplevelId, WaylandProtocol.XdgToplevel.SetMaximized, null);
			} else if (state == FormWindowState.Minimized) {
				connection.SendRequest (window.XdgToplevelId, WaylandProtocol.XdgToplevel.SetMinimized, null);
			} else {
				connection.SendRequest (window.XdgToplevelId, WaylandProtocol.XdgToplevel.UnsetMaximized, null);
			}
		}

		internal override void SetWindowMinMax (IntPtr handle, Rectangle maximized, Size min, Size max)
		{
			WaylandWindow window;
			if (!windows.TryGetValue (handle, out window) || window.XdgToplevelId == 0)
				return;

			connection.SendRequest (window.XdgToplevelId, WaylandProtocol.XdgToplevel.SetMinSize, delegate (WaylandRequestBuilder b) {
				b.WriteInt32 (min.Width);
				b.WriteInt32 (min.Height);
			});
			connection.SendRequest (window.XdgToplevelId, WaylandProtocol.XdgToplevel.SetMaxSize, delegate (WaylandRequestBuilder b) {
				b.WriteInt32 (max.Width);
				b.WriteInt32 (max.Height);
			});
		}

		internal override void SetWindowStyle (IntPtr handle, CreateParams cp)
		{
			WaylandWindow window;
			if (!windows.TryGetValue (handle, out window))
				return;

			window.Hwnd.initial_style = cp.WindowStyle;
			window.Hwnd.initial_ex_style = cp.WindowExStyle;
			SetHwndStyles (window.Hwnd, cp);
		}

		internal override double GetWindowTransparency (IntPtr handle)
		{
			WaylandWindow window;
			if (!windows.TryGetValue (handle, out window))
				return 1.0;
			return window.Hwnd.opacity / (double) UInt32.MaxValue;
		}

		internal override void SetWindowTransparency (IntPtr handle, double transparency, Color key)
		{
			WaylandWindow window;
			if (!windows.TryGetValue (handle, out window))
				return;

			transparency = Math.Max (0.0, Math.Min (1.0, transparency));
			window.Hwnd.opacity = (uint) (transparency * UInt32.MaxValue);
		}

		internal override TransparencySupport SupportsTransparency ()
		{
			return TransparencySupport.GetSet;
		}

		internal override void SetBorderStyle (IntPtr handle, FormBorderStyle borderStyle)
		{
			WaylandWindow window;
			if (windows.TryGetValue (handle, out window))
				window.Hwnd.border_style = borderStyle;
		}

		internal override void SetMenu (IntPtr handle, Menu menu)
		{
			WaylandWindow window;
			if (windows.TryGetValue (handle, out window))
				window.Hwnd.menu = menu;
		}

		internal override bool GetText (IntPtr handle, out string text)
		{
			WaylandWindow window;
			if (windows.TryGetValue (handle, out window)) {
				text = window.Text;
				return true;
			}

			text = String.Empty;
			return false;
		}

		internal override bool Text (IntPtr handle, string text)
		{
			WaylandWindow window;
			if (!windows.TryGetValue (handle, out window))
				return false;

			window.Text = text ?? String.Empty;
			if (window.XdgToplevelId != 0) {
				connection.SendRequest (window.XdgToplevelId, WaylandProtocol.XdgToplevel.SetTitle, delegate (WaylandRequestBuilder b) {
					b.WriteString (window.Text);
				});
			}
			return true;
		}

		internal override bool SetVisible (IntPtr handle, bool visible, bool activate)
		{
			WaylandWindow window;
			if (!windows.TryGetValue (handle, out window))
				return false;

			window.Hwnd.visible = visible;
			window.Hwnd.mapped = visible;

			if (visible) {
				EnsureNativeWindow (window);
				if (IsSubsurfaceWindow (window))
					Invalidate (handle, Rectangle.Empty, false);
				if (activate && !IsSubsurfaceWindow (window))
					Activate (handle);
			} else {
				UnmapNativeWindow (window);
			}

			PostMessage (handle, Msg.WM_SHOWWINDOW, visible ? (IntPtr) 1 : IntPtr.Zero, IntPtr.Zero);
			return true;
		}

		internal override bool IsVisible (IntPtr handle)
		{
			WaylandWindow window;
			return windows.TryGetValue (handle, out window) && window.Hwnd.visible;
		}

		internal override bool IsEnabled (IntPtr handle)
		{
			WaylandWindow window;
			return windows.TryGetValue (handle, out window) && window.Hwnd.Enabled;
		}

		internal override IntPtr SetParent (IntPtr handle, IntPtr parent)
		{
			WaylandWindow window;
			if (!windows.TryGetValue (handle, out window))
				return IntPtr.Zero;

			IntPtr old = window.Hwnd.Parent == null ? IntPtr.Zero : window.Hwnd.Parent.Handle;
			Hwnd oldParent = window.Hwnd.Parent;
			window.Hwnd.Parent = parent == IntPtr.Zero ? null : Hwnd.ObjectFromHandle (parent);

			if (oldParent != window.Hwnd.Parent && window.SurfaceId != 0)
				DestroyNativeWindow (window);
			if (window.Hwnd.visible) {
				EnsureNativeWindow (window);
				Invalidate (handle, Rectangle.Empty, false);
			}

			return old;
		}

		internal override IntPtr GetParent (IntPtr handle, bool withOwner)
		{
			WaylandWindow window;
			if (!windows.TryGetValue (handle, out window))
				return IntPtr.Zero;
			if (withOwner && window.Hwnd.owner != null)
				return window.Hwnd.owner.Handle;
			return window.Hwnd.Parent == null ? IntPtr.Zero : window.Hwnd.Parent.Handle;
		}

		internal override void UpdateWindow (IntPtr handle)
		{
			Invalidate (handle, Rectangle.Empty, false);
		}

		internal override PaintEventArgs PaintEventStart (ref Message msg, IntPtr handle, bool client)
		{
			WaylandWindow window;
			if (!windows.TryGetValue (handle, out window)) {
				return new PaintEventArgs (Graphics.FromImage (fallbackBitmap), new Rectangle (0, 0, 1, 1));
			}

			window.EnsureBackBuffer ();
			Rectangle clip = window.Hwnd.Invalid;
			if (clip == Rectangle.Empty)
				clip = new Rectangle (Point.Empty, window.BackBuffer.Size);

			window.Hwnd.invalid_list.Clear ();
			window.Hwnd.expose_pending = false;

			Graphics graphics = Graphics.FromImage (window.BackBuffer);
			graphics.SetClip (clip);
			return new PaintEventArgs (graphics, clip);
		}

		internal override void PaintEventEnd (ref Message msg, IntPtr handle, bool client, PaintEventArgs pevent)
		{
			if (pevent != null)
				pevent.Dispose ();

			WaylandWindow window;
			if (windows.TryGetValue (handle, out window)) {
				if (window.SurfaceId == 0 && window.Hwnd.visible)
					EnsureNativeWindow (window);
				if (window.SurfaceId == 0)
					return;
				if (window.XdgSurfaceId != 0 && !window.XdgConfigured)
					return;

				window.EnsureBackBuffer ();
				WaylandShmBuffer buffer = WaylandShmBuffer.CreateFromBitmap (connection, shmId, window.BackBuffer, window.BufferScale);
				window.Buffers.Add (buffer);
				waylandBuffers [buffer.BufferId] = buffer;

				connection.SendRequest (window.SurfaceId, WaylandProtocol.WlSurface.SetBufferScale, delegate (WaylandRequestBuilder b) {
					b.WriteInt32 (buffer.Scale);
				});
				connection.SendRequest (window.SurfaceId, WaylandProtocol.WlSurface.Attach, delegate (WaylandRequestBuilder b) {
					b.WriteObject (buffer.BufferId);
					b.WriteInt32 (0);
					b.WriteInt32 (0);
				});
				connection.SendRequest (window.SurfaceId, WaylandProtocol.WlSurface.DamageBuffer, delegate (WaylandRequestBuilder b) {
					b.WriteInt32 (0);
					b.WriteInt32 (0);
					b.WriteInt32 (buffer.BufferWidth);
					b.WriteInt32 (buffer.BufferHeight);
				});
				connection.SendRequest (window.SurfaceId, WaylandProtocol.WlSurface.Commit, null);
			}
		}

		internal Graphics CreateGraphics (IntPtr handle)
		{
			WaylandWindow window;
			if (!windows.TryGetValue (handle, out window))
				return Graphics.FromImage (fallbackBitmap);

			window.EnsureBackBuffer ();
			return Graphics.FromImage (window.BackBuffer);
		}

		internal override void SetWindowPos (IntPtr handle, int x, int y, int width, int height)
		{
			WaylandWindow window;
			if (!windows.TryGetValue (handle, out window))
				return;

			window.Hwnd.X = x;
			window.Hwnd.Y = y;
			window.Hwnd.Width = Math.Max (1, width);
			window.Hwnd.Height = Math.Max (1, height);
			window.Hwnd.ClientRect = window.Hwnd.GetClientRectangle (window.Hwnd.Width, window.Hwnd.Height);
			UpdateSubsurfacePosition (window);
			PostMessage (handle, Msg.WM_WINDOWPOSCHANGED, IntPtr.Zero, IntPtr.Zero);
		}

		internal override void GetWindowPos (IntPtr handle, bool isToplevel, out int x, out int y, out int width, out int height, out int clientWidth, out int clientHeight)
		{
			WaylandWindow window;
			if (!windows.TryGetValue (handle, out window)) {
				x = y = width = height = clientWidth = clientHeight = 0;
				return;
			}

			x = window.Hwnd.X;
			y = window.Hwnd.Y;
			width = window.Hwnd.Width;
			height = window.Hwnd.Height;
			clientWidth = window.Hwnd.ClientRect.Width;
			clientHeight = window.Hwnd.ClientRect.Height;
		}

		internal override void Activate (IntPtr handle)
		{
			if (!windows.ContainsKey (handle))
				return;

			activeWindow = handle;
			focusWindow = handle;
			PostMessage (handle, Msg.WM_ACTIVATE, (IntPtr) WindowActiveFlags.WA_ACTIVE, IntPtr.Zero);
			PostMessage (handle, Msg.WM_SETFOCUS, IntPtr.Zero, IntPtr.Zero);
		}

		internal override void EnableWindow (IntPtr handle, bool enable)
		{
			WaylandWindow window;
			if (windows.TryGetValue (handle, out window))
				window.Hwnd.enabled = enable;
		}

		internal override void SetModal (IntPtr handle, bool modal)
		{
		}

		internal override void SetAllowDrop (IntPtr handle, bool value)
		{
			WaylandWindow window;
			if (windows.TryGetValue (handle, out window))
				window.Hwnd.allow_drop = value;
		}

		internal override DragDropEffects StartDrag (IntPtr handle, object data, DragDropEffects allowedEffects)
		{
			return DragDropEffects.None;
		}

		internal override void Invalidate (IntPtr handle, Rectangle rc, bool clear)
		{
			WaylandWindow window;
			if (!windows.TryGetValue (handle, out window))
				return;

			if (rc == Rectangle.Empty)
				rc = window.Hwnd.ClientRect;
			window.Hwnd.AddInvalidArea (rc);
			window.Hwnd.expose_pending = true;
			PostMessage (handle, Msg.WM_PAINT, IntPtr.Zero, IntPtr.Zero);
		}

		internal override void InvalidateNC (IntPtr handle)
		{
			PostMessage (handle, Msg.WM_NCPAINT, IntPtr.Zero, IntPtr.Zero);
		}

		internal override IntPtr DefWndProc (ref Message msg)
		{
			return IntPtr.Zero;
		}

		internal override void HandleException (Exception e)
		{
			throw e;
		}

		internal override void DoEvents ()
		{
			MSG msg = new MSG ();
			while (PeekMessage (null, ref msg, IntPtr.Zero, 0, 0, 0)) {
				TranslateMessage (ref msg);
				DispatchMessage (ref msg);
			}
			DispatchWaylandPending (0);
		}

		internal override bool PeekMessage (object queueId, ref MSG msg, IntPtr hWnd, int wFilterMin, int wFilterMax, uint flags)
		{
			CheckTimers (DateTime.UtcNow);
			DispatchWaylandPending (0);

			object item = DequeueMessage ();
			if (item == null)
				return false;

			if (item is GCHandle) {
				XplatUIDriverSupport.ExecuteClientMessage ((GCHandle) item);
				return PeekMessage (queueId, ref msg, hWnd, wFilterMin, wFilterMax, flags);
			}

			msg = (MSG) item;
			return true;
		}

		internal override void PostQuitMessage (int exitCode)
		{
			quitPosted = true;
			PostMessage (IntPtr.Zero, Msg.WM_QUIT, (IntPtr) exitCode, IntPtr.Zero);
		}

		internal override bool GetMessage (object queueId, ref MSG msg, IntPtr hWnd, int wFilterMin, int wFilterMax)
		{
			if (quitPosted) {
				msg.hwnd = IntPtr.Zero;
				msg.message = Msg.WM_QUIT;
				return true;
			}

			if (PeekMessage (queueId, ref msg, hWnd, wFilterMin, wFilterMax, 0))
				return true;

			DispatchWaylandPending (25);
			if (PeekMessage (queueId, ref msg, hWnd, wFilterMin, wFilterMax, 0))
				return true;

			RaiseIdle (EventArgs.Empty);
			msg.hwnd = IntPtr.Zero;
			msg.message = Msg.WM_ENTERIDLE;
			return true;
		}

		internal override bool TranslateMessage (ref MSG msg)
		{
			return true;
		}

		internal override IntPtr DispatchMessage (ref MSG msg)
		{
			if (msg.message == Msg.WM_QUIT)
				return IntPtr.Zero;
			return NativeWindow.WndProc (msg.hwnd, msg.message, msg.wParam, msg.lParam);
		}

		internal override bool SetZOrder (IntPtr hWnd, IntPtr afterHWnd, bool top, bool bottom)
		{
			if (!zOrder.Remove (hWnd))
				return false;

			if (top) {
				zOrder.Add (hWnd);
			} else if (bottom) {
				zOrder.Insert (0, hWnd);
			} else {
				int index = zOrder.IndexOf (afterHWnd);
				zOrder.Insert (index < 0 ? zOrder.Count : index, hWnd);
			}

			WaylandWindow window;
			if (windows.TryGetValue (hWnd, out window))
				ApplySubsurfaceZOrder (window, afterHWnd, top, bottom);

			return true;
		}

		internal override bool SetTopmost (IntPtr hWnd, bool enabled)
		{
			WaylandWindow window;
			if (!windows.TryGetValue (hWnd, out window))
				return false;
			window.Hwnd.topmost = enabled;
			return true;
		}

		internal override bool SetOwner (IntPtr hWnd, IntPtr hWndOwner)
		{
			WaylandWindow window;
			if (!windows.TryGetValue (hWnd, out window))
				return false;
			window.Hwnd.owner = hWndOwner == IntPtr.Zero ? null : Hwnd.ObjectFromHandle (hWndOwner);
			return true;
		}

		internal override bool CalculateWindowRect (ref Rectangle clientRect, CreateParams cp, Menu menu, out Rectangle windowRect)
		{
			windowRect = Hwnd.GetWindowRectangle (cp, menu, clientRect);
			return true;
		}

		internal override Region GetClipRegion (IntPtr hwnd)
		{
			WaylandWindow window;
			if (windows.TryGetValue (hwnd, out window))
				return window.Hwnd.UserClip;
			return null;
		}

		internal override void SetClipRegion (IntPtr hwnd, Region region)
		{
			WaylandWindow window;
			if (windows.TryGetValue (hwnd, out window))
				window.Hwnd.UserClip = region;
		}

		internal override void SetCursor (IntPtr hwnd, IntPtr cursor)
		{
			if (overrideCursor != IntPtr.Zero)
				cursor = overrideCursor;

			WaylandWindow window;
			if (windows.TryGetValue (hwnd, out window))
				window.Hwnd.cursor = cursor;
		}

		internal override void ShowCursor (bool show)
		{
		}

		internal override void OverrideCursor (IntPtr cursor)
		{
			overrideCursor = cursor;
		}

		internal override IntPtr DefineCursor (Bitmap bitmap, Bitmap mask, Color cursorPixel, Color maskPixel, int xHotSpot, int yHotSpot)
		{
			return (IntPtr) bitmap.GetHashCode ();
		}

		internal override IntPtr DefineStdCursor (StdCursor id)
		{
			return (IntPtr) ((int) id + 1);
		}

		internal override Bitmap DefineStdCursorBitmap (StdCursor id)
		{
			return new Bitmap (CursorSize.Width, CursorSize.Height);
		}

		internal override void DestroyCursor (IntPtr cursor)
		{
		}

		internal override void GetCursorInfo (IntPtr cursor, out int width, out int height, out int hotspotX, out int hotspotY)
		{
			width = CursorSize.Width;
			height = CursorSize.Height;
			hotspotX = 0;
			hotspotY = 0;
		}

		internal override void GetCursorPos (IntPtr hwnd, out int x, out int y)
		{
			x = 0;
			y = 0;
		}

		internal override void SetCursorPos (IntPtr hwnd, int x, int y)
		{
		}

		internal override void ScreenToClient (IntPtr hwnd, ref int x, ref int y)
		{
			WaylandWindow window;
			if (!windows.TryGetValue (hwnd, out window))
				return;
			x -= window.Hwnd.X;
			y -= window.Hwnd.Y;
		}

		internal override void ClientToScreen (IntPtr hwnd, ref int x, ref int y)
		{
			WaylandWindow window;
			if (!windows.TryGetValue (hwnd, out window))
				return;
			x += window.Hwnd.X;
			y += window.Hwnd.Y;
		}

		internal override void GrabWindow (IntPtr hwnd, IntPtr confineToHwnd)
		{
			grabWindow = hwnd;
		}

		internal override void GrabInfo (out IntPtr hwnd, out bool grabConfined, out Rectangle grabArea)
		{
			hwnd = grabWindow;
			grabConfined = false;
			grabArea = Rectangle.Empty;
		}

		internal override void UngrabWindow (IntPtr hwnd)
		{
			if (grabWindow == hwnd)
				grabWindow = IntPtr.Zero;
		}

		internal override void SendAsyncMethod (AsyncMethodData method)
		{
			EnqueueMessage (GCHandle.Alloc (method));
		}

		internal override void SetTimer (Timer timer)
		{
			lock (timers) {
				if (!timers.Contains (timer))
					timers.Add (timer);
			}
		}

		internal override void KillTimer (Timer timer)
		{
			lock (timers) {
				timers.Remove (timer);
			}
		}

		internal override void CreateCaret (IntPtr hwnd, int width, int height)
		{
		}

		internal override void DestroyCaret (IntPtr hwnd)
		{
		}

		internal override void SetCaretPos (IntPtr hwnd, int x, int y)
		{
		}

		internal override void CaretVisible (IntPtr hwnd, bool visible)
		{
		}

		internal override IntPtr GetFocus ()
		{
			return focusWindow;
		}

		internal override void SetFocus (IntPtr hwnd)
		{
			if (windows.ContainsKey (hwnd)) {
				focusWindow = hwnd;
				PostMessage (hwnd, Msg.WM_SETFOCUS, IntPtr.Zero, IntPtr.Zero);
			}
		}

		internal override IntPtr GetActive ()
		{
			return activeWindow;
		}

		internal override IntPtr GetPreviousWindow (IntPtr hwnd)
		{
			int index = zOrder.IndexOf (hwnd);
			if (index <= 0)
				return IntPtr.Zero;
			return zOrder [index - 1];
		}

		internal override void ScrollWindow (IntPtr hwnd, Rectangle rectangle, int xAmount, int yAmount, bool withChildren)
		{
			Invalidate (hwnd, rectangle, false);
		}

		internal override void ScrollWindow (IntPtr hwnd, int xAmount, int yAmount, bool withChildren)
		{
			Invalidate (hwnd, Rectangle.Empty, false);
		}

		internal override bool GetFontMetrics (Graphics g, Font font, out int ascent, out int descent)
		{
			FontFamily family = font.FontFamily;
			int em = family.GetEmHeight (font.Style);
			ascent = (int) Math.Ceiling (font.Size * family.GetCellAscent (font.Style) / em);
			descent = (int) Math.Ceiling (font.Size * family.GetCellDescent (font.Style) / em);
			return true;
		}

		internal override bool SystrayAdd (IntPtr hwnd, string tip, Icon icon, out ToolTip tt)
		{
			tt = null;
			return false;
		}

		internal override bool SystrayChange (IntPtr hwnd, string tip, Icon icon, ref ToolTip tt)
		{
			return false;
		}

		internal override void SystrayRemove (IntPtr hwnd, ref ToolTip tt)
		{
			tt = null;
		}

		internal override void SystrayBalloon (IntPtr hwnd, int timeout, string title, string text, ToolTipIcon icon)
		{
		}

		internal override Point GetMenuOrigin (IntPtr hwnd)
		{
			WaylandWindow window;
			return windows.TryGetValue (hwnd, out window) ? window.Hwnd.MenuOrigin : Point.Empty;
		}

		internal override void MenuToScreen (IntPtr hwnd, ref int x, ref int y)
		{
			ClientToScreen (hwnd, ref x, ref y);
		}

		internal override void ScreenToMenu (IntPtr hwnd, ref int x, ref int y)
		{
			ScreenToClient (hwnd, ref x, ref y);
		}

		internal override void SetIcon (IntPtr handle, Icon icon)
		{
		}

		internal override void ClipboardClose (IntPtr handle)
		{
		}

		internal override IntPtr ClipboardOpen (bool primarySelection)
		{
			return IntPtr.Zero;
		}

		internal override int ClipboardGetID (IntPtr handle, string format)
		{
			return 0;
		}

		internal override void ClipboardStore (IntPtr handle, object obj, int id, XplatUI.ObjectToClipboard converter, bool copy)
		{
		}

		internal override int [] ClipboardAvailableFormats (IntPtr handle)
		{
			return new int [0];
		}

		internal override object ClipboardRetrieve (IntPtr handle, int id, XplatUI.ClipboardToObject converter)
		{
			return null;
		}

		internal override void DrawReversibleLine (Point start, Point end, Color backColor)
		{
		}

		internal override void DrawReversibleRectangle (IntPtr handle, Rectangle rect, int lineWidth)
		{
		}

		internal override void FillReversibleRectangle (Rectangle rectangle, Color backColor)
		{
		}

		internal override void DrawReversibleFrame (Rectangle rectangle, Color backColor, FrameStyle style)
		{
		}

		internal override SizeF GetAutoScaleSize (Font font)
		{
			return new SizeF (font.Height, font.Height);
		}

		internal override IntPtr SendMessage (IntPtr hwnd, Msg message, IntPtr wParam, IntPtr lParam)
		{
			return NativeWindow.WndProc (hwnd, message, wParam, lParam);
		}

		internal override bool PostMessage (IntPtr hwnd, Msg message, IntPtr wParam, IntPtr lParam)
		{
			MSG msg = new MSG ();
			msg.hwnd = hwnd;
			msg.message = message;
			msg.wParam = wParam;
			msg.lParam = lParam;
			msg.time = (uint) Environment.TickCount;
			EnqueueMessage (msg);
			return true;
		}

		internal override int SendInput (IntPtr hwnd, Queue keys)
		{
			return 0;
		}

		internal override object StartLoop (Thread thread)
		{
			return null;
		}

		internal override void EndLoop (Thread thread)
		{
			PostQuitMessage (0);
		}

		internal override void RequestNCRecalc (IntPtr hwnd)
		{
			PostMessage (hwnd, Msg.WM_NCCALCSIZE, IntPtr.Zero, IntPtr.Zero);
		}

		internal override void ResetMouseHover (IntPtr hwnd)
		{
		}

		internal override void RequestAdditionalWM_NCMessages (IntPtr hwnd, bool hover, bool leave)
		{
		}

		internal override void RaiseIdle (EventArgs e)
		{
			EventHandler idle = Idle;
			if (idle != null)
				idle (this, e);
		}

		void BindOutputs ()
		{
			foreach (WaylandGlobal global in registry.Globals) {
				if (global.Interface != "wl_output")
					continue;

				uint objectId = connection.Bind (registry, global, 2);
				waylandOutputs [objectId] = new WaylandOutput {
					ObjectId = objectId,
					Scale = 1,
				};
			}
		}

		void EnsureNativeWindow (WaylandWindow window)
		{
			if (window.SurfaceId != 0 || connection == null)
				return;

			if (IsSubsurfaceWindow (window)) {
				WaylandWindow parent = GetParentWindow (window);
				if (parent == null)
					return;

				EnsureNativeWindow (parent);
				if (parent.SurfaceId == 0)
					return;

				window.SurfaceId = connection.AllocateId ();
				window.SubsurfaceId = connection.AllocateId ();
				window.BufferScale = parent.BufferScale;

				connection.SendRequest (compositorId, WaylandProtocol.WlCompositor.CreateSurface, delegate (WaylandRequestBuilder b) {
					b.WriteNewId (window.SurfaceId);
				});
				connection.SendRequest (subcompositorId, WaylandProtocol.WlSubcompositor.GetSubsurface, delegate (WaylandRequestBuilder b) {
					b.WriteNewId (window.SubsurfaceId);
					b.WriteObject (window.SurfaceId);
					b.WriteObject (parent.SurfaceId);
				});
				connection.SendRequest (window.SurfaceId, WaylandProtocol.WlSurface.SetBufferScale, delegate (WaylandRequestBuilder b) {
					b.WriteInt32 (window.BufferScale);
				});
				UpdateSubsurfacePosition (window);
				connection.SendRequest (window.SubsurfaceId, WaylandProtocol.WlSubsurface.SetDesync, null);
				ApplySubsurfaceZOrder (window, IntPtr.Zero, true, false);
				connection.SendRequest (window.SurfaceId, WaylandProtocol.WlSurface.Commit, null);

				waylandObjects [window.SurfaceId] = window;
				waylandObjects [window.SubsurfaceId] = window;
				return;
			}

			window.SurfaceId = connection.AllocateId ();
			window.XdgSurfaceId = connection.AllocateId ();
			window.XdgToplevelId = connection.AllocateId ();

			connection.SendRequest (compositorId, WaylandProtocol.WlCompositor.CreateSurface, delegate (WaylandRequestBuilder b) {
				b.WriteNewId (window.SurfaceId);
			});
			connection.SendRequest (xdgWmBaseId, WaylandProtocol.XdgWmBase.GetXdgSurface, delegate (WaylandRequestBuilder b) {
				b.WriteNewId (window.XdgSurfaceId);
				b.WriteObject (window.SurfaceId);
			});
			connection.SendRequest (window.XdgSurfaceId, WaylandProtocol.XdgSurface.GetToplevel, delegate (WaylandRequestBuilder b) {
				b.WriteNewId (window.XdgToplevelId);
			});
			connection.SendRequest (window.XdgToplevelId, WaylandProtocol.XdgToplevel.SetTitle, delegate (WaylandRequestBuilder b) {
				b.WriteString (window.Text);
			});
			connection.SendRequest (window.XdgToplevelId, WaylandProtocol.XdgToplevel.SetAppId, delegate (WaylandRequestBuilder b) {
				b.WriteString ("mono-winforms");
			});
			connection.SendRequest (window.SurfaceId, WaylandProtocol.WlSurface.SetBufferScale, delegate (WaylandRequestBuilder b) {
				b.WriteInt32 (window.BufferScale);
			});
			connection.SendRequest (window.SurfaceId, WaylandProtocol.WlSurface.Commit, null);

			waylandObjects [window.SurfaceId] = window;
			waylandObjects [window.XdgSurfaceId] = window;
			waylandObjects [window.XdgToplevelId] = window;
		}

		void DestroyNativeWindow (WaylandWindow window)
		{
			if (connection == null)
				return;

			foreach (WaylandShmBuffer buffer in window.Buffers) {
				waylandBuffers.Remove (buffer.BufferId);
				buffer.DestroyWaylandObject (connection);
			}
			window.Buffers.Clear ();

			if (window.XdgToplevelId != 0)
				connection.SendRequest (window.XdgToplevelId, WaylandProtocol.XdgToplevel.Destroy, null);
			if (window.XdgSurfaceId != 0)
				connection.SendRequest (window.XdgSurfaceId, WaylandProtocol.XdgSurface.Destroy, null);
			if (window.SubsurfaceId != 0)
				connection.SendRequest (window.SubsurfaceId, WaylandProtocol.WlSubsurface.Destroy, null);
			if (window.SurfaceId != 0)
				connection.SendRequest (window.SurfaceId, WaylandProtocol.WlSurface.Destroy, null);

			waylandObjects.Remove (window.SurfaceId);
			waylandObjects.Remove (window.SubsurfaceId);
			waylandObjects.Remove (window.XdgSurfaceId);
			waylandObjects.Remove (window.XdgToplevelId);
			window.SurfaceId = 0;
			window.SubsurfaceId = 0;
			window.XdgSurfaceId = 0;
			window.XdgToplevelId = 0;
			window.XdgConfigured = false;
		}

		void UnmapNativeWindow (WaylandWindow window)
		{
			if (connection == null || window.SurfaceId == 0)
				return;

			connection.SendRequest (window.SurfaceId, WaylandProtocol.WlSurface.Attach, delegate (WaylandRequestBuilder b) {
				b.WriteObject (0);
				b.WriteInt32 (0);
				b.WriteInt32 (0);
			});
			connection.SendRequest (window.SurfaceId, WaylandProtocol.WlSurface.Commit, null);
		}

		void DestroyChildNativeWindows (WaylandWindow parent)
		{
			List<WaylandWindow> children = new List<WaylandWindow> ();
			foreach (WaylandWindow candidate in windows.Values) {
				if (candidate.Hwnd.Parent == parent.Hwnd)
					children.Add (candidate);
			}

			foreach (WaylandWindow child in children) {
				DestroyChildNativeWindows (child);
				DestroyNativeWindow (child);
			}
		}

		void DispatchWaylandPending (int timeoutMilliseconds)
		{
			if (connection == null)
				return;

			try {
				WaylandMessage message;
				while (connection.TryReadMessage (timeoutMilliseconds, out message)) {
					HandleWaylandMessage (message);
					timeoutMilliseconds = 0;
				}
			} catch (Exception e) {
				Console.Error.WriteLine ("Wayland dispatch failed: {0}", e.Message);
			}
		}

		void HandleWaylandMessage (WaylandMessage message)
		{
			if (message.ObjectId == 1) {
				if (message.Opcode == WaylandProtocol.WlDisplay.Error) {
					WaylandMessageReader reader = new WaylandMessageReader (message.Payload);
					uint objectId = reader.ReadUInt32 ();
					uint code = reader.ReadUInt32 ();
					string text = reader.ReadString ();
					throw new InvalidOperationException ("Wayland protocol error on object " + objectId + ", code " + code + ": " + text);
				}
				return;
			}

			if (message.ObjectId == xdgWmBaseId && message.Opcode == WaylandProtocol.XdgWmBase.Ping) {
				WaylandMessageReader reader = new WaylandMessageReader (message.Payload);
				uint serial = reader.ReadUInt32 ();
				connection.SendRequest (xdgWmBaseId, WaylandProtocol.XdgWmBase.Pong, delegate (WaylandRequestBuilder b) {
					b.WriteUInt32 (serial);
				});
				return;
			}

			WaylandOutput output;
			if (waylandOutputs.TryGetValue (message.ObjectId, out output)) {
				HandleOutputMessage (output, message);
				return;
			}

			WaylandShmBuffer releasedBuffer;
			if (waylandBuffers.TryGetValue (message.ObjectId, out releasedBuffer)) {
				if (message.Opcode == WaylandProtocol.WlBuffer.Release) {
					waylandBuffers.Remove (releasedBuffer.BufferId);
					foreach (WaylandWindow candidate in windows.Values)
						candidate.Buffers.Remove (releasedBuffer);
					releasedBuffer.DestroyWaylandObject (connection);
				}
				return;
			}

			WaylandWindow window;
			if (!waylandObjects.TryGetValue (message.ObjectId, out window))
				return;

			if (message.ObjectId == window.SurfaceId) {
				HandleSurfaceMessage (window, message);
				return;
			}

			if (message.ObjectId == window.XdgSurfaceId && message.Opcode == WaylandProtocol.XdgSurface.Configure) {
				WaylandMessageReader reader = new WaylandMessageReader (message.Payload);
				uint serial = reader.ReadUInt32 ();
				connection.SendRequest (window.XdgSurfaceId, WaylandProtocol.XdgSurface.AckConfigure, delegate (WaylandRequestBuilder b) {
					b.WriteUInt32 (serial);
				});
				window.XdgConfigured = true;
				Invalidate (window.Hwnd.Handle, Rectangle.Empty, false);
				connection.SendRequest (window.SurfaceId, WaylandProtocol.WlSurface.Commit, null);
				return;
			}

			if (message.ObjectId == window.XdgToplevelId) {
				if (message.Opcode == WaylandProtocol.XdgToplevel.Configure) {
					HandleToplevelConfigure (window, message);
					return;
				}

				if (message.Opcode == WaylandProtocol.XdgToplevel.Close) {
					PostMessage (window.Hwnd.Handle, Msg.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
					return;
				}
			}
		}

		void HandleOutputMessage (WaylandOutput output, WaylandMessage message)
		{
			if (message.Opcode != WaylandProtocol.WlOutput.Scale)
				return;

			WaylandMessageReader reader = new WaylandMessageReader (message.Payload);
			int scale = Math.Max (1, reader.ReadInt32 ());
			if (scale == output.Scale)
				return;

			output.Scale = scale;
			foreach (WaylandWindow window in windows.Values) {
				if (window.EnteredOutputs.Contains (output.ObjectId))
					UpdateWindowScale (window);
			}
		}

		void HandleSurfaceMessage (WaylandWindow window, WaylandMessage message)
		{
			WaylandMessageReader reader = new WaylandMessageReader (message.Payload);

			if (message.Opcode == WaylandProtocol.WlSurface.Enter) {
				window.EnteredOutputs.Add (reader.ReadUInt32 ());
				UpdateWindowScale (window);
				return;
			}

			if (message.Opcode == WaylandProtocol.WlSurface.Leave) {
				window.EnteredOutputs.Remove (reader.ReadUInt32 ());
				UpdateWindowScale (window);
			}
		}

		void UpdateWindowScale (WaylandWindow window)
		{
			int scale = GetTargetScale (window);

			if (scale == window.BufferScale)
				return;

			window.BufferScale = scale;
			if (window.SurfaceId != 0) {
				connection.SendRequest (window.SurfaceId, WaylandProtocol.WlSurface.SetBufferScale, delegate (WaylandRequestBuilder b) {
					b.WriteInt32 (window.BufferScale);
				});
				Invalidate (window.Hwnd.Handle, Rectangle.Empty, false);
			}

			foreach (WaylandWindow child in windows.Values) {
				if (child.Hwnd.Parent == window.Hwnd)
					UpdateWindowScale (child);
			}
		}

		int GetTargetScale (WaylandWindow window)
		{
			WaylandWindow parent = GetParentWindow (window);
			if (parent != null)
				return parent.BufferScale;

			int scale = 1;
			foreach (uint outputId in window.EnteredOutputs) {
				WaylandOutput output;
				if (waylandOutputs.TryGetValue (outputId, out output))
					scale = Math.Max (scale, output.Scale);
			}
			return scale;
		}

		void HandleToplevelConfigure (WaylandWindow window, WaylandMessage message)
		{
			WaylandMessageReader reader = new WaylandMessageReader (message.Payload);
			int width = reader.ReadInt32 ();
			int height = reader.ReadInt32 ();
			if (!reader.End)
				reader.ReadArray ();

			if (width <= 0 || height <= 0)
				return;
			if (width == window.Hwnd.Width && height == window.Hwnd.Height)
				return;

			window.Hwnd.Width = width;
			window.Hwnd.Height = height;
			window.Hwnd.ClientRect = window.Hwnd.GetClientRectangle (width, height);
			window.Dispose ();
			PostMessage (window.Hwnd.Handle, Msg.WM_WINDOWPOSCHANGED, IntPtr.Zero, IntPtr.Zero);
			Invalidate (window.Hwnd.Handle, Rectangle.Empty, false);
		}

		WaylandWindow GetParentWindow (WaylandWindow window)
		{
			if (window.Hwnd.Parent == null)
				return null;

			WaylandWindow parent;
			return windows.TryGetValue (window.Hwnd.Parent.Handle, out parent) ? parent : null;
		}

		bool IsSubsurfaceWindow (WaylandWindow window)
		{
			return window.Hwnd.Parent != null || StyleSet ((int) window.Hwnd.initial_style, WindowStyles.WS_CHILD);
		}

		void UpdateSubsurfacePosition (WaylandWindow window)
		{
			if (window.SubsurfaceId == 0)
				return;

			connection.SendRequest (window.SubsurfaceId, WaylandProtocol.WlSubsurface.SetPosition, delegate (WaylandRequestBuilder b) {
				b.WriteInt32 (window.Hwnd.X);
				b.WriteInt32 (window.Hwnd.Y);
			});
		}

		void ApplySubsurfaceZOrder (WaylandWindow window, IntPtr afterHWnd, bool top, bool bottom)
		{
			if (window.SubsurfaceId == 0)
				return;

			WaylandWindow parent = GetParentWindow (window);
			if (parent == null || parent.SurfaceId == 0)
				return;

			uint targetSurface = parent.SurfaceId;
			WaylandWindow sibling;
			if (!bottom && afterHWnd != IntPtr.Zero && windows.TryGetValue (afterHWnd, out sibling) && sibling.Hwnd.Parent == window.Hwnd.Parent && sibling.SurfaceId != 0)
				targetSurface = sibling.SurfaceId;
			else if (top) {
				foreach (IntPtr handle in zOrder) {
					if (handle == window.Hwnd.Handle)
						continue;
					if (windows.TryGetValue (handle, out sibling) && sibling.Hwnd.Parent == window.Hwnd.Parent && sibling.SurfaceId != 0)
						targetSurface = sibling.SurfaceId;
				}
			}

			connection.SendRequest (window.SubsurfaceId, WaylandProtocol.WlSubsurface.PlaceAbove, delegate (WaylandRequestBuilder b) {
				b.WriteObject (targetSurface);
			});
		}

		void CheckTimers (DateTime now)
		{
			lock (timers) {
				for (int i = 0; i < timers.Count; i++) {
					Timer timer = timers [i];
					if (timer.Enabled && timer.Expires <= now) {
						timer.FireTick ();
						timer.Update (now);
					}
				}
			}
		}

		void EnqueueMessage (object item)
		{
			lock (queueLock) {
				messageQueue.Enqueue (item);
			}
		}

		object DequeueMessage ()
		{
			lock (queueLock) {
				if (messageQueue.Count == 0)
					return null;
				return messageQueue.Dequeue ();
			}
		}

		IntPtr AllocateSyntheticHandle ()
		{
			return (IntPtr) Interlocked.Increment (ref nextHandle);
		}

		void SendParentNotify (IntPtr child, Msg cause, int x, int y)
		{
			Hwnd hwnd = Hwnd.GetObjectFromWindow (child);
			if (hwnd == null || hwnd.Parent == null)
				return;

			SendMessage (hwnd.Parent.Handle, Msg.WM_PARENTNOTIFY, Control.MakeParam ((int) cause, 0), child);
			SendParentNotify (hwnd.Parent.Handle, cause, x, y);
		}

		void SetHwndStyles (Hwnd hwnd, CreateParams cp)
		{
			hwnd.border_static = false;
			hwnd.caption_height = 0;
			hwnd.tool_caption_height = 0;
			hwnd.title_style = TitleStyle.None;
			hwnd.border_style = FormBorderStyle.None;

			if (StyleSet (cp.Style, WindowStyles.WS_CAPTION)) {
				hwnd.title_style = ExStyleSet (cp.ExStyle, WindowExStyles.WS_EX_TOOLWINDOW) ? TitleStyle.Tool : TitleStyle.Normal;
				hwnd.caption_height = CaptionHeight;
				hwnd.tool_caption_height = ToolWindowCaptionHeight;
			}

			if (StyleSet (cp.Style, WindowStyles.WS_THICKFRAME)) {
				hwnd.border_style = ExStyleSet (cp.ExStyle, WindowExStyles.WS_EX_TOOLWINDOW) ? FormBorderStyle.SizableToolWindow : FormBorderStyle.Sizable;
			} else if (StyleSet (cp.Style, WindowStyles.WS_BORDER)) {
				hwnd.border_style = FormBorderStyle.FixedSingle;
			} else if (ExStyleSet (cp.ExStyle, WindowExStyles.WS_EX_CLIENTEDGE) || ExStyleSet (cp.ExStyle, WindowExStyles.WS_EX_STATICEDGE)) {
				hwnd.border_style = FormBorderStyle.Fixed3D;
				hwnd.border_static = ExStyleSet (cp.ExStyle, WindowExStyles.WS_EX_STATICEDGE);
			}
		}

		static bool StyleSet (int style, WindowStyles flag)
		{
			return (style & (int) flag) == (int) flag;
		}

		static bool ExStyleSet (int style, WindowExStyles flag)
		{
			return (style & (int) flag) == (int) flag;
		}
	}
}
