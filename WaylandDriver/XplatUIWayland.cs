using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
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
			public uint XdgPopupId;
			public bool XdgConfigured;
			public readonly List<WaylandShmBuffer> Buffers = new List<WaylandShmBuffer> ();
			public readonly HashSet<uint> EnteredOutputs = new HashSet<uint> ();

			public void EnsureBackBuffer ()
			{
				Rectangle client = Hwnd.ClientRect;
				// WinForms keeps using logical pixels.  The backing bitmap is in
				// Wayland buffer pixels; ApplyLogicalScale maps logical drawing
				// into this larger physical bitmap for HiDPI outputs.
				int width = checked (Math.Max (1, client.Width) * Math.Max (1, BufferScale));
				int height = checked (Math.Max (1, client.Height) * Math.Max (1, BufferScale));

				if (BackBuffer != null && BackBuffer.Width == width && BackBuffer.Height == height)
					return;

				if (BackBuffer != null)
					BackBuffer.Dispose ();

				BackBuffer = new Bitmap (width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
				BackBuffer.SetResolution (96.0f * Math.Max (1, BufferScale), 96.0f * Math.Max (1, BufferScale));
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

		sealed class WaylandCaret {
			public Timer Timer;
			public IntPtr Hwnd;
			public int X;
			public int Y;
			public int Width = 1;
			public int Height = 1;
			public bool Visible;
			public bool On;
		}

		// Mono's Control.DoubleBuffer uses XplatUI offscreen drawables.  These
		// must be scaled the same way as real surfaces or double-buffered labels,
		// buttons, etc. paint into 1x bitmaps and get enlarged later.
		sealed class WaylandOffscreenDrawable : IDisposable {
			public readonly IntPtr Handle;
			public readonly int LogicalWidth;
			public readonly int LogicalHeight;
			public Bitmap Bitmap;
			public int Scale = 1;

			public WaylandOffscreenDrawable (IntPtr handle, int width, int height)
			{
				Handle = handle;
				LogicalWidth = Math.Max (1, width);
				LogicalHeight = Math.Max (1, height);
			}

			public void EnsureBitmap (int scale)
			{
				scale = Math.Max (1, scale);
				int width = checked (LogicalWidth * scale);
				int height = checked (LogicalHeight * scale);

				if (Bitmap != null && Scale == scale && Bitmap.Width == width && Bitmap.Height == height)
					return;

				if (Bitmap != null)
					Bitmap.Dispose ();

				Bitmap = new Bitmap (width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
				Bitmap.SetResolution (96.0f * scale, 96.0f * scale);
				Scale = scale;
			}

			public void Dispose ()
			{
				if (Bitmap != null)
					Bitmap.Dispose ();
				Bitmap = null;
			}
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
		readonly HashSet<uint> evdevKeysDown = new HashSet<uint> ();
		readonly Dictionary<string, char> keyText = new Dictionary<string, char> ();
		readonly WaylandCaret caret = new WaylandCaret ();

		WaylandConnection connection;
		WaylandRegistry registry;
		uint compositorId;
		uint subcompositorId;
		uint shmId;
		uint xdgWmBaseId;
		uint seatId;
		uint pointerId;
		uint keyboardId;
		uint lastInputSerial;
		int nextHandle = 0x4000;
		IntPtr activeWindow = IntPtr.Zero;
		IntPtr focusWindow = IntPtr.Zero;
		IntPtr grabWindow = IntPtr.Zero;
		IntPtr overrideCursor = IntPtr.Zero;
		WaylandWindow pointerWindow;
		WaylandWindow keyboardWindow;
		Point pointerSurfacePosition;
		Point mousePosition;
		MouseButtons mouseButtons;
		Keys modifierKeys;
		IntPtr lastClickWindow = IntPtr.Zero;
		Msg lastClickMessage;
		int lastClickX;
		int lastClickY;
		uint lastClickTime;
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

		internal override MouseButtons MouseButtons {
			get { return mouseButtons; }
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

		internal override Keys ModifierKeys {
			get { return modifierKeys; }
		}

		internal override IntPtr InitializeDriver ()
		{
			connection = WaylandConnection.ConnectFromEnvironment ();
			registry = connection.GetRegistryRoundtrip ();
			compositorId = connection.Bind (registry, "wl_compositor", 4);
			subcompositorId = connection.Bind (registry, "wl_subcompositor", 1);
			shmId = connection.Bind (registry, "wl_shm", 1);
			xdgWmBaseId = connection.Bind (registry, "xdg_wm_base", 3);
			seatId = connection.Bind (registry, "wl_seat", 5);
			BindOutputs ();

			if (compositorId == 0)
				throw new InvalidOperationException ("The Wayland compositor did not advertise wl_compositor.");
			if (subcompositorId == 0)
				throw new InvalidOperationException ("The Wayland compositor did not advertise wl_subcompositor.");
			if (shmId == 0)
				throw new InvalidOperationException ("The Wayland compositor did not advertise wl_shm.");
			if (xdgWmBaseId == 0)
				throw new InvalidOperationException ("The Wayland compositor did not advertise xdg_wm_base.");
			// Input is optional at the protocol level.  A compositor without
			// wl_seat can still render windows, but there is no native source for
			// pointer or keyboard messages to feed into Mono's normal queue.

			return (IntPtr) 1;
		}

		internal override void ShutdownDriver (IntPtr token)
		{
			foreach (WaylandWindow window in windows.Values)
				window.Dispose ();
			foreach (WaylandShmBuffer buffer in waylandBuffers.Values)
				buffer.Dispose ();
			fallbackBitmap.Dispose ();
			if (caret.Timer != null)
				caret.Timer.Dispose ();
			caret.Timer = null;
			caret.Hwnd = IntPtr.Zero;
			windows.Clear ();
			waylandObjects.Clear ();
			waylandBuffers.Clear ();
			waylandOutputs.Clear ();
			evdevKeysDown.Clear ();
			keyText.Clear ();

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
			DestroyCaret (handle);
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
			if (grabWindow == handle)
				grabWindow = IntPtr.Zero;
			if (pointerWindow == window)
				pointerWindow = null;
			if (keyboardWindow == window)
				keyboardWindow = null;
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
				if (activate && !IsSubsurfaceWindow (window) && !IsPopupWindow (window))
					Activate (handle);
			} else {
				if (IsPopupWindow (window)) {
					// xdg_popup is a transient interaction role, not just a
					// hidden toplevel.  Destroy it on hide so the next show gets a
					// fresh positioner and input serial for the popup grab.
					DestroyNativeWindow (window);
				} else {
					UnmapNativeWindow (window);
				}
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
			if (clip == Rectangle.Empty) {
				Rectangle clientRect = window.Hwnd.ClientRect;
				clip = new Rectangle (0, 0, Math.Max (1, clientRect.Width), Math.Max (1, clientRect.Height));
			}

			window.Hwnd.invalid_list.Clear ();
			window.Hwnd.expose_pending = false;

			Graphics graphics = Graphics.FromImage (window.BackBuffer);
			// The PaintEventArgs clip remains logical.  Only the Graphics target
			// is scaled, so control layout and paint code see normal WinForms units.
			ApplyLogicalScale (graphics, window);
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

				CommitWindowBuffer (window);
			}
		}

		internal Graphics CreateGraphics (IntPtr handle)
		{
			WaylandWindow window;
			if (!windows.TryGetValue (handle, out window))
				return Graphics.FromImage (fallbackBitmap);

			window.EnsureBackBuffer ();
			Graphics graphics = Graphics.FromImage (window.BackBuffer);
			ApplyLogicalScale (graphics, window);
			return graphics;
		}

		internal override void CreateOffscreenDrawable (IntPtr handle, int width, int height, out object offscreen_drawable)
		{
			WaylandOffscreenDrawable drawable = new WaylandOffscreenDrawable (handle, width, height);
			drawable.EnsureBitmap (GetTargetScale (handle));
			offscreen_drawable = drawable;
		}

		internal override void DestroyOffscreenDrawable (object offscreen_drawable)
		{
			WaylandOffscreenDrawable drawable = (WaylandOffscreenDrawable) offscreen_drawable;
			drawable.Dispose ();
		}

		internal override Graphics GetOffscreenGraphics (object offscreen_drawable)
		{
			WaylandOffscreenDrawable drawable = (WaylandOffscreenDrawable) offscreen_drawable;
			drawable.EnsureBitmap (GetTargetScale (drawable.Handle));
			Graphics graphics = Graphics.FromImage (drawable.Bitmap);
			// Mono paints into offscreen drawables with logical coordinates too.
			// Keep that coordinate system and only increase backing pixels.
			ApplyLogicalScale (graphics, drawable.Scale);
			return graphics;
		}

		internal override void BlitFromOffscreen (IntPtr dest_handle, Graphics dest_dc, object offscreen_drawable, Graphics offscreen_dc, Rectangle r)
		{
			WaylandOffscreenDrawable drawable = (WaylandOffscreenDrawable) offscreen_drawable;
			Rectangle dest = Rectangle.Intersect (r, new Rectangle (0, 0, drawable.LogicalWidth, drawable.LogicalHeight));
			if (dest.Width <= 0 || dest.Height <= 0)
				return;

			// The destination rectangle is still logical WinForms space, but the
			// offscreen bitmap stores physical pixels.
			Rectangle source = new Rectangle (dest.X * drawable.Scale, dest.Y * drawable.Scale, dest.Width * drawable.Scale, dest.Height * drawable.Scale);
			InterpolationMode oldInterpolation = dest_dc.InterpolationMode;

			try {
				dest_dc.InterpolationMode = InterpolationMode.NearestNeighbor;
				dest_dc.DrawImage (drawable.Bitmap, dest, source, GraphicsUnit.Pixel);
			} finally {
				dest_dc.InterpolationMode = oldInterpolation;
			}
		}

		internal override void SetWindowPos (IntPtr handle, int x, int y, int width, int height)
		{
			WaylandWindow window;
			if (!windows.TryGetValue (handle, out window))
				return;

			int oldWidth = window.Hwnd.Width;
			int oldHeight = window.Hwnd.Height;
			bool popupRolePositionChanged = window.XdgPopupId != 0 &&
				(window.Hwnd.X != x || window.Hwnd.Y != y || oldWidth != width || oldHeight != height);

			window.Hwnd.X = x;
			window.Hwnd.Y = y;
			window.Hwnd.Width = Math.Max (1, width);
			window.Hwnd.Height = Math.Max (1, height);
			window.Hwnd.ClientRect = window.Hwnd.GetClientRectangle (window.Hwnd.Width, window.Hwnd.Height);
			if (popupRolePositionChanged) {
				// xdg_popup geometry comes from the immutable xdg_positioner used
				// at role creation.  When Mono moves/resizes a visible popup HWND,
				// recreate the surface role so the compositor gets the new anchor.
				DestroyNativeWindow (window);
				EnsureNativeWindow (window);
			}
			UpdateSubsurfacePosition (window);
			PostMessage (handle, Msg.WM_WINDOWPOSCHANGED, IntPtr.Zero, IntPtr.Zero);

			// A Wayland subsurface's visible extent is the last committed buffer
			// size, not Mono's Hwnd bounds.  If a child grows without repainting,
			// the compositor can keep showing the old narrower buffer even though
			// WinForms layout has already changed the logical client size.
			if (window.Hwnd.visible && (window.Hwnd.Width != oldWidth || window.Hwnd.Height != oldHeight))
				Invalidate (handle, Rectangle.Empty, false);
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
			PostMessage (handle, Msg.WM_ACTIVATE, (IntPtr) WindowActiveFlags.WA_ACTIVE, IntPtr.Zero);
			SetFocusWindow (handle);
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
			char ch;
			string key = GetKeyTextKey (msg);
			if (!keyText.TryGetValue (key, out ch))
				return true;

			keyText.Remove (key);
			// Mono's Application loop calls TranslateMessage between its
			// keyboard-capture/preprocessing checks and DispatchMessage.  Post
			// WM_CHAR here, not directly from wl_keyboard.key, so shortcut and
			// dialog-key filtering sees the same message order as the other
			// native drivers.
			PostInputMessage (msg.hwnd, msg.message == Msg.WM_SYSKEYDOWN ? Msg.WM_SYSCHAR : Msg.WM_CHAR, (IntPtr) ch, msg.lParam, msg.time);
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
			bool wasPopup = IsPopupWindow (window);
			window.Hwnd.owner = hWndOwner == IntPtr.Zero ? null : Hwnd.ObjectFromHandle (hWndOwner);
			bool isPopup = IsPopupWindow (window);
			if (wasPopup != isPopup && window.SurfaceId != 0) {
				// ComboBox and ToolStrip popup HWNDs may be shown before Mono
				// assigns their owner.  Wayland roles are immutable once a
				// surface has become xdg_toplevel/xdg_popup, so ownership changes
				// require recreating the native surface with the correct role.
				DestroyNativeWindow (window);
				if (window.Hwnd.visible) {
					EnsureNativeWindow (window);
					Invalidate (hWnd, Rectangle.Empty, false);
				}
			}
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
			x = mousePosition.X;
			y = mousePosition.Y;
		}

		internal override void SetCursorPos (IntPtr hwnd, int x, int y)
		{
		}

		internal override void ScreenToClient (IntPtr hwnd, ref int x, ref int y)
		{
			WaylandWindow window;
			if (!windows.TryGetValue (hwnd, out window))
				return;
			Point screen = GetWindowScreenLocation (window);
			x -= screen.X;
			y -= screen.Y;
		}

		internal override void ClientToScreen (IntPtr hwnd, ref int x, ref int y)
		{
			WaylandWindow window;
			if (!windows.TryGetValue (hwnd, out window))
				return;
			Point screen = GetWindowScreenLocation (window);
			x += screen.X;
			y += screen.Y;
		}

		internal override void GrabWindow (IntPtr hwnd, IntPtr confineToHwnd)
		{
			if (grabWindow != IntPtr.Zero && grabWindow != hwnd)
				SendMessage (grabWindow, Msg.WM_CAPTURECHANGED, IntPtr.Zero, IntPtr.Zero);
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
			if (grabWindow == hwnd) {
				grabWindow = IntPtr.Zero;
				// Control.Capture relies on WM_CAPTURECHANGED to clear managed
				// capture state when the driver releases a grab.
				SendMessage (hwnd, Msg.WM_CAPTURECHANGED, IntPtr.Zero, IntPtr.Zero);
			}
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
			if (!windows.ContainsKey (hwnd))
				return;

			bool wasVisible = caret.Hwnd == hwnd && caret.Visible;
			bool wasOn = caret.Hwnd == hwnd && caret.On;
			if (caret.Hwnd != IntPtr.Zero && caret.Hwnd != hwnd)
				DestroyCaret (caret.Hwnd);

			EnsureCaretTimer ();
			caret.Hwnd = hwnd;
			caret.Width = Math.Max (1, width);
			caret.Height = Math.Max (1, height);
			caret.Visible = wasVisible;
			caret.On = wasVisible && wasOn;
			if (caret.Visible) {
				if (caret.On)
					CommitCaretWindow (hwnd);
				caret.Timer.Start ();
			}
		}

		internal override void DestroyCaret (IntPtr hwnd)
		{
			if (caret.Hwnd != hwnd)
				return;

			bool needsCommit = caret.On;
			if (caret.Timer != null)
				caret.Timer.Stop ();
			caret.Visible = false;
			caret.On = false;
			caret.Hwnd = IntPtr.Zero;
			if (needsCommit)
				CommitCaretWindow (hwnd);
		}

		internal override void SetCaretPos (IntPtr hwnd, int x, int y)
		{
			if (caret.Hwnd != hwnd)
				return;

			caret.X = x;
			caret.Y = y;
			if (caret.Visible) {
				caret.On = true;
				CommitCaretWindow (hwnd);
				if (caret.Timer != null)
					caret.Timer.Start ();
			}
		}

		internal override void CaretVisible (IntPtr hwnd, bool visible)
		{
			if (caret.Hwnd != hwnd)
				return;

			if (visible) {
				EnsureCaretTimer ();
				caret.Visible = true;
				caret.On = true;
				CommitCaretWindow (hwnd);
				caret.Timer.Start ();
			} else {
				if (caret.Timer != null)
					caret.Timer.Stop ();
				bool needsCommit = caret.On;
				caret.Visible = false;
				caret.On = false;
				if (needsCommit)
					CommitCaretWindow (hwnd);
			}
		}

		internal override IntPtr GetFocus ()
		{
			return focusWindow;
		}

		internal override void SetFocus (IntPtr hwnd)
		{
			SetFocusWindow (hwnd);
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

			// WinForms child HWNDs are native child windows, not independent
			// desktop windows.  Model them as subsurfaces attached to the parent
			// surface; only root/form windows get xdg_toplevel roles.
			if (IsSubsurfaceWindow (window)) {
				WaylandWindow parent = GetParentWindow (window);
				if (parent == null)
					return;

				EnsureNativeWindow (parent);
				if (parent.SurfaceId == 0)
					return;

				window.SurfaceId = connection.AllocateId ();
				window.SubsurfaceId = connection.AllocateId ();
				// Parent and child must share one logical coordinate system.
				// Scaling only changes buffer density, never child bounds.
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
				// Mono paints HWNDs independently.  Desync lets a child commit its
				// new buffer without waiting for an explicit parent commit.
				connection.SendRequest (window.SubsurfaceId, WaylandProtocol.WlSubsurface.SetDesync, null);
				ApplySubsurfaceZOrder (window, IntPtr.Zero, true, false);
				connection.SendRequest (window.SurfaceId, WaylandProtocol.WlSurface.Commit, null);

				waylandObjects [window.SurfaceId] = window;
				waylandObjects [window.SubsurfaceId] = window;
				return;
			}

			if (IsPopupWindow (window)) {
				CreatePopupNativeWindow (window);
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

		void CreatePopupNativeWindow (WaylandWindow window)
		{
			WaylandWindow parent = GetPopupParentWindow (window);
			if (parent == null)
				return;

			EnsureNativeWindow (parent);
			if (parent.XdgSurfaceId == 0)
				return;

			window.SurfaceId = connection.AllocateId ();
			window.XdgSurfaceId = connection.AllocateId ();
			window.XdgPopupId = connection.AllocateId ();
			uint positionerId = connection.AllocateId ();
			window.BufferScale = parent.BufferScale;

			Point parentScreen = GetWindowScreenLocation (parent);
			Point popupScreen = GetWindowScreenLocation (window);
			int relativeX = popupScreen.X - parentScreen.X;
			int relativeY = popupScreen.Y - parentScreen.Y;
			int width = Math.Max (1, window.Hwnd.Width);
			int height = Math.Max (1, window.Hwnd.Height);

			connection.SendRequest (compositorId, WaylandProtocol.WlCompositor.CreateSurface, delegate (WaylandRequestBuilder b) {
				b.WriteNewId (window.SurfaceId);
			});
			connection.SendRequest (xdgWmBaseId, WaylandProtocol.XdgWmBase.GetXdgSurface, delegate (WaylandRequestBuilder b) {
				b.WriteNewId (window.XdgSurfaceId);
				b.WriteObject (window.SurfaceId);
			});
			connection.SendRequest (xdgWmBaseId, WaylandProtocol.XdgWmBase.CreatePositioner, delegate (WaylandRequestBuilder b) {
				b.WriteNewId (positionerId);
			});
			connection.SendRequest (positionerId, WaylandProtocol.XdgPositioner.SetSize, delegate (WaylandRequestBuilder b) {
				b.WriteInt32 (width);
				b.WriteInt32 (height);
			});
			connection.SendRequest (positionerId, WaylandProtocol.XdgPositioner.SetAnchorRect, delegate (WaylandRequestBuilder b) {
				b.WriteInt32 (relativeX);
				b.WriteInt32 (relativeY);
				b.WriteInt32 (1);
				b.WriteInt32 (1);
			});
			connection.SendRequest (positionerId, WaylandProtocol.XdgPositioner.SetAnchor, delegate (WaylandRequestBuilder b) {
				b.WriteUInt32 (WaylandProtocol.XdgPositioner.AnchorTopLeft);
			});
			connection.SendRequest (positionerId, WaylandProtocol.XdgPositioner.SetGravity, delegate (WaylandRequestBuilder b) {
				b.WriteUInt32 (WaylandProtocol.XdgPositioner.GravityBottomRight);
			});
			connection.SendRequest (positionerId, WaylandProtocol.XdgPositioner.SetConstraintAdjustment, delegate (WaylandRequestBuilder b) {
				b.WriteUInt32 (WaylandProtocol.XdgPositioner.ConstraintAdjustmentSlideX |
					WaylandProtocol.XdgPositioner.ConstraintAdjustmentSlideY |
					WaylandProtocol.XdgPositioner.ConstraintAdjustmentFlipX |
					WaylandProtocol.XdgPositioner.ConstraintAdjustmentFlipY |
					WaylandProtocol.XdgPositioner.ConstraintAdjustmentResizeX |
					WaylandProtocol.XdgPositioner.ConstraintAdjustmentResizeY);
			});
			// Owned WinForms popup HWNDs are not independent application
			// windows.  xdg_popup gives the compositor the same relationship
			// that Mono expresses through Hwnd.owner, including outside-click
			// dismissal for grabbed popup menus and combo lists.
			connection.SendRequest (window.XdgSurfaceId, WaylandProtocol.XdgSurface.GetPopup, delegate (WaylandRequestBuilder b) {
				b.WriteNewId (window.XdgPopupId);
				b.WriteObject (parent.XdgSurfaceId);
				b.WriteObject (positionerId);
			});
			connection.SendRequest (positionerId, WaylandProtocol.XdgPositioner.Destroy, null);
			if (seatId != 0 && lastInputSerial != 0) {
				// The xdg_popup grab is tied to a user-input serial by design;
				// without that serial the compositor must reject the grab.  The
				// popup still maps, but outside-click dismissal is compositor
				// managed only for user-triggered popups.
				connection.SendRequest (window.XdgPopupId, WaylandProtocol.XdgPopup.Grab, delegate (WaylandRequestBuilder b) {
					b.WriteObject (seatId);
					b.WriteUInt32 (lastInputSerial);
				});
			}
			connection.SendRequest (window.SurfaceId, WaylandProtocol.WlSurface.SetBufferScale, delegate (WaylandRequestBuilder b) {
				b.WriteInt32 (window.BufferScale);
			});
			connection.SendRequest (window.SurfaceId, WaylandProtocol.WlSurface.Commit, null);

			waylandObjects [window.SurfaceId] = window;
			waylandObjects [window.XdgSurfaceId] = window;
			waylandObjects [window.XdgPopupId] = window;
		}

		void CommitWindowBuffer (WaylandWindow window)
		{
			if (connection == null || window.SurfaceId == 0)
				return;
			if (window.XdgSurfaceId != 0 && !window.XdgConfigured)
				return;

			window.EnsureBackBuffer ();
			Bitmap commitBitmap = window.BackBuffer;
			Bitmap caretBitmap = null;

			try {
				if (caret.Hwnd == window.Hwnd.Handle && caret.Visible && caret.On) {
					// The caret is driver-owned state, not part of the control's
					// painting.  Draw it onto a temporary commit bitmap so blink
					// toggles never corrupt the pristine WinForms backing store.
					caretBitmap = new Bitmap (window.BackBuffer);
					DrawCaretOverlay (window, caretBitmap);
					commitBitmap = caretBitmap;
				}

				WaylandShmBuffer buffer = WaylandShmBuffer.CreateFromBitmap (connection, shmId, commitBitmap, window.BufferScale);
				window.Buffers.Add (buffer);
				waylandBuffers [buffer.BufferId] = buffer;

				// wl_surface.damage_buffer is in physical buffer pixels.  The
				// buffer scale tells the compositor how those pixels map back to
				// the surface's logical size.
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
			} finally {
				if (caretBitmap != null)
					caretBitmap.Dispose ();
			}
		}

		void DrawCaretOverlay (WaylandWindow window, Bitmap bitmap)
		{
			int scale = Math.Max (1, window.BufferScale);
			Rectangle rect = new Rectangle (
				caret.X * scale,
				caret.Y * scale,
				Math.Max (1, caret.Width * scale),
				Math.Max (1, caret.Height * scale));
			rect.Intersect (new Rectangle (0, 0, bitmap.Width, bitmap.Height));
			if (rect.Width <= 0 || rect.Height <= 0)
				return;

			Control control = Control.FromHandle (window.Hwnd.Handle);
			Color color = control == null ? SystemColors.WindowText : control.ForeColor;
			if (color.A == 0)
				color = SystemColors.WindowText;

			using (Graphics graphics = Graphics.FromImage (bitmap))
			using (Brush brush = new SolidBrush (color)) {
				graphics.FillRectangle (brush, rect);
			}
		}

		void EnsureCaretTimer ()
		{
			if (caret.Timer != null)
				return;

			caret.Timer = new Timer ();
			caret.Timer.Interval = Math.Max (1, CaretBlinkTime);
			caret.Timer.Tick += CaretTimerTick;
		}

		void CaretTimerTick (object sender, EventArgs e)
		{
			if (!caret.Visible || caret.Hwnd == IntPtr.Zero) {
				if (caret.Timer != null)
					caret.Timer.Stop ();
				return;
			}

			caret.On = !caret.On;
			CommitCaretWindow (caret.Hwnd);
		}

		void CommitCaretWindow (IntPtr hwnd)
		{
			WaylandWindow window;
			if (!windows.TryGetValue (hwnd, out window))
				return;
			if (!window.Hwnd.visible || window.SurfaceId == 0)
				return;
			CommitWindowBuffer (window);
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

			if (window.XdgPopupId != 0)
				connection.SendRequest (window.XdgPopupId, WaylandProtocol.XdgPopup.Destroy, null);
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
			waylandObjects.Remove (window.XdgPopupId);
			window.SurfaceId = 0;
			window.SubsurfaceId = 0;
			window.XdgSurfaceId = 0;
			window.XdgToplevelId = 0;
			window.XdgPopupId = 0;
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

			if (message.ObjectId == seatId) {
				HandleSeatMessage (message);
				return;
			}

			if (message.ObjectId == pointerId) {
				HandlePointerMessage (message);
				return;
			}

			if (message.ObjectId == keyboardId) {
				HandleKeyboardMessage (message);
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

			if (message.ObjectId == window.XdgPopupId) {
				HandlePopupMessage (window, message);
				return;
			}
		}

		void HandleSeatMessage (WaylandMessage message)
		{
			if (message.Opcode != WaylandProtocol.WlSeat.Capabilities)
				return;

			WaylandMessageReader reader = new WaylandMessageReader (message.Payload);
			uint capabilities = reader.ReadUInt32 ();

			if ((capabilities & WaylandProtocol.WlSeat.CapabilityPointer) != 0) {
				if (pointerId == 0) {
					pointerId = connection.AllocateId ();
					connection.SendRequest (seatId, WaylandProtocol.WlSeat.GetPointer, delegate (WaylandRequestBuilder b) {
						b.WriteNewId (pointerId);
					});
				}
			} else if (pointerId != 0) {
				connection.SendRequest (pointerId, WaylandProtocol.WlPointer.Release, null);
				pointerId = 0;
				pointerWindow = null;
				mouseButtons = MouseButtons.None;
			}

			if ((capabilities & WaylandProtocol.WlSeat.CapabilityKeyboard) != 0) {
				if (keyboardId == 0) {
					keyboardId = connection.AllocateId ();
					connection.SendRequest (seatId, WaylandProtocol.WlSeat.GetKeyboard, delegate (WaylandRequestBuilder b) {
						b.WriteNewId (keyboardId);
					});
				}
			} else if (keyboardId != 0) {
				connection.SendRequest (keyboardId, WaylandProtocol.WlKeyboard.Release, null);
				keyboardId = 0;
				keyboardWindow = null;
				evdevKeysDown.Clear ();
				modifierKeys = Keys.None;
			}
		}

		void HandlePointerMessage (WaylandMessage message)
		{
			WaylandMessageReader reader = new WaylandMessageReader (message.Payload);

			switch (message.Opcode) {
			case WaylandProtocol.WlPointer.Enter: {
				lastInputSerial = reader.ReadUInt32 ();
				uint surfaceId = reader.ReadUInt32 ();
				int x = FixedToInt (reader.ReadInt32 ());
				int y = FixedToInt (reader.ReadInt32 ());

				WaylandWindow window;
				if (!waylandObjects.TryGetValue (surfaceId, out window))
					return;

				pointerWindow = window;
				UpdatePointerPosition (window, x, y);
				PostPointerMessage (Msg.WM_MOUSE_ENTER, window, x, y, 0, CurrentMessageTime ());
				PostPointerMessage (Msg.WM_MOUSEMOVE, window, x, y, 0, CurrentMessageTime ());
				return;
			}

			case WaylandProtocol.WlPointer.Leave: {
				lastInputSerial = reader.ReadUInt32 ();
				uint surfaceId = reader.ReadUInt32 ();

				WaylandWindow window;
				if (!waylandObjects.TryGetValue (surfaceId, out window))
					return;

				if (grabWindow == IntPtr.Zero)
					PostPointerMessage (Msg.WM_MOUSELEAVE, window, pointerSurfacePosition.X, pointerSurfacePosition.Y, 0, CurrentMessageTime ());
				if (pointerWindow == window)
					pointerWindow = null;
				return;
			}

			case WaylandProtocol.WlPointer.Motion: {
				uint time = reader.ReadUInt32 ();
				int x = FixedToInt (reader.ReadInt32 ());
				int y = FixedToInt (reader.ReadInt32 ());
				if (pointerWindow == null)
					return;

				UpdatePointerPosition (pointerWindow, x, y);
				PostPointerMessage (Msg.WM_MOUSEMOVE, pointerWindow, x, y, 0, time);
				return;
			}

			case WaylandProtocol.WlPointer.Button:
				HandlePointerButton (reader);
				return;

			case WaylandProtocol.WlPointer.Axis:
				HandlePointerAxis (reader);
				return;
			}
		}

		void HandlePointerButton (WaylandMessageReader reader)
		{
			lastInputSerial = reader.ReadUInt32 ();
			uint time = reader.ReadUInt32 ();
			uint button = reader.ReadUInt32 ();
			uint state = reader.ReadUInt32 ();

			Msg down;
			Msg up;
			Msg dblclk;
			MouseButtons managedButton;
			if (!TryGetMouseMessages (button, out managedButton, out down, out up, out dblclk))
				return;

			if (state == WaylandProtocol.WlPointer.ButtonStatePressed)
				mouseButtons |= managedButton;
			else
				mouseButtons &= ~managedButton;

			WaylandWindow surfaceWindow = pointerWindow;
			if (surfaceWindow == null)
				return;

			int targetX;
			int targetY;
			WaylandWindow target = ResolvePointerTarget (surfaceWindow, pointerSurfacePosition.X, pointerSurfacePosition.Y, out targetX, out targetY);
			if (target == null)
				return;

			if (state == WaylandProtocol.WlPointer.ButtonStatePressed) {
				ActivateForInput (target);
				SetFocusWindow (target.Hwnd.Handle);
				SendParentNotify (target.Hwnd.Handle, down, targetX, targetY);
				Msg message = IsDoubleClick (target.Hwnd.Handle, down, targetX, targetY, time) ? dblclk : down;
				PostInputMessage (target.Hwnd.Handle, message, (IntPtr) MakeMouseWParam (0), Control.MakeParam (targetX, targetY), time);
				lastClickWindow = target.Hwnd.Handle;
				lastClickMessage = down;
				lastClickX = targetX;
				lastClickY = targetY;
				lastClickTime = time;
			} else {
				PostInputMessage (target.Hwnd.Handle, up, (IntPtr) MakeMouseWParam (0), Control.MakeParam (targetX, targetY), time);
			}
		}

		void HandlePointerAxis (WaylandMessageReader reader)
		{
			uint time = reader.ReadUInt32 ();
			uint axis = reader.ReadUInt32 ();
			int value = reader.ReadInt32 ();
			if (axis != WaylandProtocol.WlPointer.AxisVerticalScroll || value == 0)
				return;

			WaylandWindow surfaceWindow = pointerWindow;
			if (surfaceWindow == null)
				return;

			int targetX;
			int targetY;
			WaylandWindow pointerTarget = ResolvePointerTarget (surfaceWindow, pointerSurfacePosition.X, pointerSurfacePosition.Y, out targetX, out targetY);
			WaylandWindow wheelTarget = null;
			if (focusWindow != IntPtr.Zero)
				windows.TryGetValue (focusWindow, out wheelTarget);
			if (wheelTarget == null)
				wheelTarget = pointerTarget;
			if (wheelTarget == null)
				return;

			if (wheelTarget != pointerTarget) {
				Point screen = GetWindowScreenLocation (wheelTarget);
				targetX = mousePosition.X - screen.X;
				targetY = mousePosition.Y - screen.Y;
			}

			// Wayland's vertical axis is positive when scrolling down; WinForms
			// uses the Win32 convention where positive wheel deltas scroll up.
			int delta = value < 0 ? 120 : -120;
			PostInputMessage (wheelTarget.Hwnd.Handle, Msg.WM_MOUSEWHEEL, (IntPtr) MakeMouseWParam (delta), Control.MakeParam (targetX, targetY), time);
		}

		void HandleKeyboardMessage (WaylandMessage message)
		{
			WaylandMessageReader reader = new WaylandMessageReader (message.Payload);

			switch (message.Opcode) {
			case WaylandProtocol.WlKeyboard.Keymap:
				// This intentionally ignores the compositor keymap for now.
				// wl_keyboard sends XKB keymaps through file descriptors, and a
				// correct managed implementation needs an XKB parser instead of
				// libxkbcommon.  Until that exists, key routing is physical
				// evdev-to-Keys plus US ASCII text for TranslateMessage.
				return;

			case WaylandProtocol.WlKeyboard.Enter: {
				lastInputSerial = reader.ReadUInt32 ();
				uint surfaceId = reader.ReadUInt32 ();
				if (!reader.End)
					reader.ReadArray ();

				WaylandWindow window;
				if (!waylandObjects.TryGetValue (surfaceId, out window))
					return;

				keyboardWindow = window;
				ActivateForInput (window);
				if (focusWindow == IntPtr.Zero || !FocusBelongsToSurfaceTree (window))
					SetFocusWindow (window.Hwnd.Handle);
				return;
			}

			case WaylandProtocol.WlKeyboard.Leave:
				lastInputSerial = reader.ReadUInt32 ();
				keyboardWindow = null;
				SetFocusWindow (IntPtr.Zero);
				return;

			case WaylandProtocol.WlKeyboard.Key:
				HandleKeyboardKey (reader);
				return;

			case WaylandProtocol.WlKeyboard.Modifiers:
				lastInputSerial = reader.ReadUInt32 ();
				// The depressed/latched/locked masks are XKB-keymap dependent.
				// With no managed XKB layer yet, keep modifier state from the
				// physical key events we route below instead of guessing mask bits.
				return;
			}
		}

		void HandleKeyboardKey (WaylandMessageReader reader)
		{
			lastInputSerial = reader.ReadUInt32 ();
			uint time = reader.ReadUInt32 ();
			uint evdevKey = reader.ReadUInt32 ();
			uint state = reader.ReadUInt32 ();
			bool pressed = state == WaylandProtocol.WlKeyboard.KeyStatePressed;
			bool wasDown = evdevKeysDown.Contains (evdevKey);
			Keys oldModifiers = modifierKeys;

			if (pressed)
				evdevKeysDown.Add (evdevKey);
			else
				evdevKeysDown.Remove (evdevKey);
			UpdateModifierKeys ();

			Keys key = MapEvdevKey (evdevKey);
			if (key == Keys.None)
				return;

			IntPtr target = focusWindow;
			// The compositor gives keyboard focus to a Wayland surface, while
			// WinForms focus is usually a child HWND selected by mouse/tab
			// handling.  Prefer Mono's focused control and fall back to the
			// compositor surface only when Mono has no focused HWND.
			if (target == IntPtr.Zero && keyboardWindow != null)
				target = keyboardWindow.Hwnd.Handle;
			if (target == IntPtr.Zero)
				target = activeWindow;
			if (target == IntPtr.Zero)
				return;

			Keys sysModifiers = pressed ? modifierKeys : oldModifiers;
			bool sysKey = (sysModifiers & Keys.Alt) != 0 && (sysModifiers & Keys.Control) == 0;
			Msg message = pressed ? (sysKey ? Msg.WM_SYSKEYDOWN : Msg.WM_KEYDOWN) : (sysKey ? Msg.WM_SYSKEYUP : Msg.WM_KEYUP);
			IntPtr lParam = MakeKeyLParam (evdevKey, pressed, wasDown, sysKey);

			MSG msg = CreateInputMessage (target, message, (IntPtr) key, lParam, time);
			char ch;
			if (pressed && TryGetKeyChar (key, modifierKeys, sysKey, out ch))
				keyText [GetKeyTextKey (msg)] = ch;
			EnqueueMessage (msg);
		}

		void HandlePopupMessage (WaylandWindow window, WaylandMessage message)
		{
			if (message.Opcode != WaylandProtocol.XdgPopup.PopupDone)
				return;

			// xdg_popup.popup_done is the compositor-side result of the popup
			// grab ending, usually from an outside click or Escape.  Convert that
			// back to the managed popup controls that know how to restore ComboBox
			// and ToolStrip state.
			DismissPopupWindow (window);
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
				else if (IsPopupWindow (child) && GetPopupParentWindow (child) == window)
					UpdateWindowScale (child);
			}
		}

		int GetTargetScale (WaylandWindow window)
		{
			WaylandWindow parent = GetParentWindow (window);
			if (parent == null && IsPopupWindow (window))
				parent = GetPopupParentWindow (window);
			// Child and owned popup scale follows the parent so SetPosition,
			// positioners, and WinForms layout remain in one logical coordinate
			// space across all related HWND surfaces.
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

		int GetTargetScale (IntPtr handle)
		{
			WaylandWindow window;
			return windows.TryGetValue (handle, out window) ? GetTargetScale (window) : 1;
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

		bool IsPopupWindow (WaylandWindow window)
		{
			return window.Hwnd.owner != null && StyleSet ((int) window.Hwnd.initial_style, WindowStyles.WS_POPUP);
		}

		WaylandWindow GetPopupParentWindow (WaylandWindow window)
		{
			if (window.Hwnd.owner == null)
				return null;

			WaylandWindow parent;
			if (!windows.TryGetValue (window.Hwnd.owner.Handle, out parent))
				return null;

			return GetRootWindow (parent);
		}

		WaylandWindow GetRootWindow (WaylandWindow window)
		{
			WaylandWindow current = window;
			while (current != null && current.Hwnd.Parent != null) {
				WaylandWindow parent;
				if (!windows.TryGetValue (current.Hwnd.Parent.Handle, out parent))
					break;
				current = parent;
			}
			return current;
		}

		Point GetWindowScreenLocation (WaylandWindow window)
		{
			int x = window.Hwnd.X;
			int y = window.Hwnd.Y;
			Hwnd parent = window.Hwnd.Parent;

			// Child HWND coordinates are parent-relative in Mono.  Wayland
			// pointer coordinates are surface-local, so every conversion through
			// Control.MousePosition/PointToClient needs the full logical parent
			// chain, not only the immediate Hwnd.X/Y.
			while (parent != null) {
				x += parent.X;
				y += parent.Y;
				parent = parent.Parent;
			}

			return new Point (x, y);
		}

		void SetFocusWindow (IntPtr hwnd)
		{
			if (hwnd != IntPtr.Zero && !windows.ContainsKey (hwnd))
				return;
			if (focusWindow == hwnd)
				return;

			IntPtr oldFocus = focusWindow;
			focusWindow = hwnd;
			if (oldFocus != IntPtr.Zero)
				PostMessage (oldFocus, Msg.WM_KILLFOCUS, hwnd, IntPtr.Zero);
			if (hwnd != IntPtr.Zero)
				PostMessage (hwnd, Msg.WM_SETFOCUS, oldFocus, IntPtr.Zero);
		}

		void ActivateForInput (WaylandWindow window)
		{
			WaylandWindow active = IsPopupWindow (window) ? GetPopupParentWindow (window) : GetRootWindow (window);
			if (active == null)
				return;

			IntPtr handle = active.Hwnd.Handle;
			if (activeWindow == handle)
				return;

			IntPtr oldActive = activeWindow;
			activeWindow = handle;
			if (oldActive != IntPtr.Zero)
				PostMessage (oldActive, Msg.WM_ACTIVATE, (IntPtr) WindowActiveFlags.WA_INACTIVE, handle);
			PostMessage (handle, Msg.WM_ACTIVATE, (IntPtr) WindowActiveFlags.WA_ACTIVE, oldActive);
		}

		bool FocusBelongsToSurfaceTree (WaylandWindow surfaceWindow)
		{
			WaylandWindow focused;
			if (focusWindow == IntPtr.Zero || !windows.TryGetValue (focusWindow, out focused))
				return false;

			WaylandWindow focusedRoot = IsPopupWindow (focused) ? GetPopupParentWindow (focused) : GetRootWindow (focused);
			WaylandWindow surfaceRoot = IsPopupWindow (surfaceWindow) ? GetPopupParentWindow (surfaceWindow) : GetRootWindow (surfaceWindow);
			return focusedRoot != null && focusedRoot == surfaceRoot;
		}

		void UpdatePointerPosition (WaylandWindow window, int x, int y)
		{
			pointerSurfacePosition = new Point (x, y);
			Point origin = GetWindowScreenLocation (window);
			mousePosition = new Point (origin.X + x, origin.Y + y);
		}

		WaylandWindow ResolvePointerTarget (WaylandWindow surfaceWindow, int surfaceX, int surfaceY, out int targetX, out int targetY)
		{
			WaylandWindow target = surfaceWindow;
			targetX = surfaceX;
			targetY = surfaceY;

			if (grabWindow != IntPtr.Zero) {
				if (!windows.TryGetValue (grabWindow, out target))
					return null;

				Point targetScreen = GetWindowScreenLocation (target);
				targetX = mousePosition.X - targetScreen.X;
				targetY = mousePosition.Y - targetScreen.Y;
			}

			return target;
		}

		void PostPointerMessage (Msg message, WaylandWindow surfaceWindow, int surfaceX, int surfaceY, int wheelDelta, uint time)
		{
			int targetX;
			int targetY;
			WaylandWindow target = ResolvePointerTarget (surfaceWindow, surfaceX, surfaceY, out targetX, out targetY);
			if (target == null)
				return;

			PostInputMessage (target.Hwnd.Handle, message, (IntPtr) MakeMouseWParam (wheelDelta), Control.MakeParam (targetX, targetY), time);
		}

		bool TryGetMouseMessages (uint button, out MouseButtons managedButton, out Msg down, out Msg up, out Msg dblclk)
		{
			switch (button) {
			case WaylandProtocol.WlPointer.ButtonLeft:
				managedButton = MouseButtons.Left;
				down = Msg.WM_LBUTTONDOWN;
				up = Msg.WM_LBUTTONUP;
				dblclk = Msg.WM_LBUTTONDBLCLK;
				return true;
			case WaylandProtocol.WlPointer.ButtonRight:
				managedButton = MouseButtons.Right;
				down = Msg.WM_RBUTTONDOWN;
				up = Msg.WM_RBUTTONUP;
				dblclk = Msg.WM_RBUTTONDBLCLK;
				return true;
			case WaylandProtocol.WlPointer.ButtonMiddle:
				managedButton = MouseButtons.Middle;
				down = Msg.WM_MBUTTONDOWN;
				up = Msg.WM_MBUTTONUP;
				dblclk = Msg.WM_MBUTTONDBLCLK;
				return true;
			default:
				managedButton = MouseButtons.None;
				down = up = dblclk = Msg.WM_NULL;
				return false;
			}
		}

		int MakeMouseWParam (int wheelDelta)
		{
			int value = 0;
			if ((mouseButtons & MouseButtons.Left) != 0)
				value |= (int) MsgButtons.MK_LBUTTON;
			if ((mouseButtons & MouseButtons.Right) != 0)
				value |= (int) MsgButtons.MK_RBUTTON;
			if ((mouseButtons & MouseButtons.Middle) != 0)
				value |= (int) MsgButtons.MK_MBUTTON;
			if ((modifierKeys & Keys.Shift) != 0)
				value |= (int) MsgButtons.MK_SHIFT;
			if ((modifierKeys & Keys.Control) != 0)
				value |= (int) MsgButtons.MK_CONTROL;

			return unchecked (value | (((int) (ushort) wheelDelta) << 16));
		}

		bool IsDoubleClick (IntPtr handle, Msg message, int x, int y, uint time)
		{
			if (lastClickWindow != handle || lastClickMessage != message)
				return false;
			if (unchecked (time - lastClickTime) > (uint) DoubleClickTime)
				return false;

			Size size = DoubleClickSize;
			return Math.Abs (x - lastClickX) <= size.Width && Math.Abs (y - lastClickY) <= size.Height;
		}

		void DismissPopupWindow (WaylandWindow window)
		{
			Control control = Control.FromHandle (window.Hwnd.Handle);
			if (control == null) {
				PostMessage (window.Hwnd.Handle, Msg.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
				return;
			}

			ToolStripDropDown dropDown = control as ToolStripDropDown;
			if (dropDown != null) {
				dropDown.Dismiss (ToolStripDropDownCloseReason.AppClicked);
				return;
			}

			System.Reflection.MethodInfo hideWindow = control.GetType ().GetMethod ("HideWindow", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
			if (hideWindow != null) {
				hideWindow.Invoke (control, null);
				return;
			}

			control.Hide ();
		}

		void PostInputMessage (IntPtr hwnd, Msg message, IntPtr wParam, IntPtr lParam, uint time)
		{
			EnqueueMessage (CreateInputMessage (hwnd, message, wParam, lParam, time));
		}

		MSG CreateInputMessage (IntPtr hwnd, Msg message, IntPtr wParam, IntPtr lParam, uint time)
		{
			MSG msg = new MSG ();
			msg.hwnd = hwnd;
			msg.message = message;
			msg.wParam = wParam;
			msg.lParam = lParam;
			msg.time = time == 0 ? CurrentMessageTime () : time;
			return msg;
		}

		static uint CurrentMessageTime ()
		{
			return unchecked ((uint) Environment.TickCount);
		}

		static int FixedToInt (int value)
		{
			return (int) Math.Floor (value / 256.0);
		}

		static IntPtr MakeKeyLParam (uint evdevKey, bool pressed, bool wasDown, bool sysKey)
		{
			int value = 1 | unchecked ((int) ((evdevKey & 0xff) << 16));
			if (sysKey)
				value |= 1 << 29;
			if (wasDown)
				value |= 1 << 30;
			if (!pressed)
				value |= unchecked ((int) 0x80000000);
			return (IntPtr) value;
		}

		static string GetKeyTextKey (MSG msg)
		{
			return msg.hwnd.ToInt64 ().ToString ("x") + ":" + ((int) msg.message).ToString ("x") + ":" +
				msg.wParam.ToInt64 ().ToString ("x") + ":" + msg.lParam.ToInt64 ().ToString ("x") + ":" +
				msg.time.ToString ("x");
		}

		void UpdateModifierKeys ()
		{
			Keys keys = Keys.None;
			if (evdevKeysDown.Contains (42) || evdevKeysDown.Contains (54))
				keys |= Keys.Shift;
			if (evdevKeysDown.Contains (29) || evdevKeysDown.Contains (97))
				keys |= Keys.Control;
			if (evdevKeysDown.Contains (56) || evdevKeysDown.Contains (100))
				keys |= Keys.Alt;
			modifierKeys = keys;
		}

		static Keys MapEvdevKey (uint key)
		{
			switch (key) {
			case 1: return Keys.Escape;
			case 2: return Keys.D1;
			case 3: return Keys.D2;
			case 4: return Keys.D3;
			case 5: return Keys.D4;
			case 6: return Keys.D5;
			case 7: return Keys.D6;
			case 8: return Keys.D7;
			case 9: return Keys.D8;
			case 10: return Keys.D9;
			case 11: return Keys.D0;
			case 12: return Keys.OemMinus;
			case 13: return Keys.Oemplus;
			case 14: return Keys.Back;
			case 15: return Keys.Tab;
			case 16: return Keys.Q;
			case 17: return Keys.W;
			case 18: return Keys.E;
			case 19: return Keys.R;
			case 20: return Keys.T;
			case 21: return Keys.Y;
			case 22: return Keys.U;
			case 23: return Keys.I;
			case 24: return Keys.O;
			case 25: return Keys.P;
			case 26: return Keys.OemOpenBrackets;
			case 27: return Keys.OemCloseBrackets;
			case 28: return Keys.Return;
			case 29: return Keys.ControlKey;
			case 30: return Keys.A;
			case 31: return Keys.S;
			case 32: return Keys.D;
			case 33: return Keys.F;
			case 34: return Keys.G;
			case 35: return Keys.H;
			case 36: return Keys.J;
			case 37: return Keys.K;
			case 38: return Keys.L;
			case 39: return Keys.OemSemicolon;
			case 40: return Keys.OemQuotes;
			case 41: return Keys.Oemtilde;
			case 42: return Keys.ShiftKey;
			case 43: return Keys.OemPipe;
			case 44: return Keys.Z;
			case 45: return Keys.X;
			case 46: return Keys.C;
			case 47: return Keys.V;
			case 48: return Keys.B;
			case 49: return Keys.N;
			case 50: return Keys.M;
			case 51: return Keys.Oemcomma;
			case 52: return Keys.OemPeriod;
			case 53: return Keys.OemQuestion;
			case 54: return Keys.ShiftKey;
			case 55: return Keys.Multiply;
			case 56: return Keys.Menu;
			case 57: return Keys.Space;
			case 58: return Keys.CapsLock;
			case 59: return Keys.F1;
			case 60: return Keys.F2;
			case 61: return Keys.F3;
			case 62: return Keys.F4;
			case 63: return Keys.F5;
			case 64: return Keys.F6;
			case 65: return Keys.F7;
			case 66: return Keys.F8;
			case 67: return Keys.F9;
			case 68: return Keys.F10;
			case 69: return Keys.NumLock;
			case 70: return Keys.Scroll;
			case 71: return Keys.NumPad7;
			case 72: return Keys.NumPad8;
			case 73: return Keys.NumPad9;
			case 74: return Keys.Subtract;
			case 75: return Keys.NumPad4;
			case 76: return Keys.NumPad5;
			case 77: return Keys.NumPad6;
			case 78: return Keys.Add;
			case 79: return Keys.NumPad1;
			case 80: return Keys.NumPad2;
			case 81: return Keys.NumPad3;
			case 82: return Keys.NumPad0;
			case 83: return Keys.Decimal;
			case 86: return Keys.OemBackslash;
			case 87: return Keys.F11;
			case 88: return Keys.F12;
			case 96: return Keys.Return;
			case 97: return Keys.ControlKey;
			case 98: return Keys.Divide;
			case 100: return Keys.Menu;
			case 102: return Keys.Home;
			case 103: return Keys.Up;
			case 104: return Keys.PageUp;
			case 105: return Keys.Left;
			case 106: return Keys.Right;
			case 107: return Keys.End;
			case 108: return Keys.Down;
			case 109: return Keys.PageDown;
			case 110: return Keys.Insert;
			case 111: return Keys.Delete;
			case 125: return Keys.LWin;
			case 126: return Keys.RWin;
			default: return Keys.None;
			}
		}

		static bool TryGetKeyChar (Keys key, Keys modifiers, bool sysKey, out char ch)
		{
			ch = '\0';
			if ((modifiers & Keys.Control) != 0)
				return false;

			bool shift = (modifiers & Keys.Shift) != 0;
			if (key >= Keys.A && key <= Keys.Z) {
				ch = (char) ((shift ? 'A' : 'a') + ((int) key - (int) Keys.A));
				return true;
			}

			if (key >= Keys.D0 && key <= Keys.D9) {
				string normal = "0123456789";
				string shifted = ")!@#$%^&*(";
				int index = (int) key - (int) Keys.D0;
				ch = shift ? shifted [index] : normal [index];
				return true;
			}

			switch (key) {
			case Keys.Space: ch = ' '; return true;
			case Keys.Return: ch = '\r'; return true;
			case Keys.Tab: ch = '\t'; return true;
			case Keys.Back: ch = '\b'; return true;
			case Keys.OemMinus: ch = shift ? '_' : '-'; return true;
			case Keys.Oemplus: ch = shift ? '+' : '='; return true;
			case Keys.OemOpenBrackets: ch = shift ? '{' : '['; return true;
			case Keys.OemCloseBrackets: ch = shift ? '}' : ']'; return true;
			case Keys.OemPipe: ch = shift ? '|' : '\\'; return true;
			case Keys.OemSemicolon: ch = shift ? ':' : ';'; return true;
			case Keys.OemQuotes: ch = shift ? '"' : '\''; return true;
			case Keys.Oemtilde: ch = shift ? '~' : '`'; return true;
			case Keys.Oemcomma: ch = shift ? '<' : ','; return true;
			case Keys.OemPeriod: ch = shift ? '>' : '.'; return true;
			case Keys.OemQuestion: ch = shift ? '?' : '/'; return true;
			default:
				return false;
			}
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

		static void ApplyLogicalScale (Graphics graphics, WaylandWindow window)
		{
			ApplyLogicalScale (graphics, window.BufferScale);
		}

		static void ApplyLogicalScale (Graphics graphics, int scale)
		{
			// This is the HiDPI boundary: controls still paint in logical pixels;
			// the transform only maps those logical pixels to a denser buffer.
			graphics.CompositingQuality = CompositingQuality.HighQuality;
			graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
			graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

			if (scale != 1)
				graphics.ScaleTransform (scale, scale);
		}

		void ApplySubsurfaceZOrder (WaylandWindow window, IntPtr afterHWnd, bool top, bool bottom)
		{
			if (window.SubsurfaceId == 0)
				return;

			WaylandWindow parent = GetParentWindow (window);
			if (parent == null || parent.SurfaceId == 0)
				return;

			// wl_subsurface.place_above wants a Wayland surface object.  The
			// parent surface is the bottom reference; otherwise translate Mono's
			// sibling HWND ordering to sibling surface ids.
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
