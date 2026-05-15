using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Mono.Unix;
using Mono.Unix.Native;
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
			public bool BufferAttached;
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

		sealed class WaylandCursor {
			public IntPtr Handle;
			public bool Standard;
			public StdCursor StandardId;
			public Bitmap Bitmap;
			public int HotspotX;
			public int HotspotY;
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

		sealed class WaylandKeymap {
			public readonly uint Format;
			public readonly byte [] Bytes;
			public readonly string Text;

			WaylandKeymap (uint format, byte [] bytes, string text)
			{
				Format = format;
				Bytes = bytes;
				Text = text ?? String.Empty;
			}

			public static unsafe WaylandKeymap ReadFromFd (uint format, int fd, uint size)
			{
				byte [] bytes = new byte [checked ((int) size)];
				try {
					int offset = 0;
					fixed (byte* data = bytes) {
						while (offset < bytes.Length) {
							long n = Syscall.read (fd, (IntPtr) (data + offset), (ulong) (bytes.Length - offset));
							if (n < 0)
								UnixMarshal.ThrowExceptionForLastError ();
							if (n == 0)
								break;
							offset += (int) n;
						}
					}

					if (offset != bytes.Length)
						Array.Resize (ref bytes, offset);

					int textLength = bytes.Length;
					if (textLength > 0 && bytes [textLength - 1] == 0)
						textLength--;
					string text = format == WaylandProtocol.WlKeyboard.KeymapFormatXkbV1 ?
						Encoding.UTF8.GetString (bytes, 0, textLength) : String.Empty;
					return new WaylandKeymap (format, bytes, text);
				} finally {
					Syscall.close (fd);
				}
			}
		}

		struct WaylandKeyResult {
			public Keys KeyCode;
			public string Text;
		}

		interface IWaylandKeyboardLayout : IDisposable {
			Keys ModifierKeys { get; }
			void SetModifiers (uint depressed, uint latched, uint locked, uint group);
			WaylandKeyResult TranslateKey (uint evdevKey, bool pressed);
		}

		sealed class PhysicalUsKeyboardLayout : IWaylandKeyboardLayout {
			readonly HashSet<uint> keysDown = new HashSet<uint> ();
			Keys modifierKeys;

			public Keys ModifierKeys {
				get { return modifierKeys; }
			}

			public void SetModifiers (uint depressed, uint latched, uint locked, uint group)
			{
				// Without an XKB keymap, the masks are not meaningful.  Keep
				// modifier state from physical key events instead.
			}

			public WaylandKeyResult TranslateKey (uint evdevKey, bool pressed)
			{
				if (pressed)
					keysDown.Add (evdevKey);
				else
					keysDown.Remove (evdevKey);
				UpdateModifierKeys ();

				Keys key = MapEvdevKey (evdevKey);
				WaylandKeyResult result = new WaylandKeyResult ();
				result.KeyCode = key;
				char ch;
				if (pressed && TryGetKeyChar (key, modifierKeys, false, out ch))
					result.Text = new string (ch, 1);
				return result;
			}

			public void Dispose ()
			{
			}

			void UpdateModifierKeys ()
			{
				Keys keys = Keys.None;
				if (keysDown.Contains (42) || keysDown.Contains (54))
					keys |= Keys.Shift;
				if (keysDown.Contains (29) || keysDown.Contains (97))
					keys |= Keys.Control;
				if (keysDown.Contains (56) || keysDown.Contains (100))
					keys |= Keys.Alt;
				modifierKeys = keys;
			}
		}

		sealed class ManagedXkbKeyboardLayout : IWaylandKeyboardLayout {
			public static IWaylandKeyboardLayout TryCreate (WaylandKeymap keymap, out string diagnostic)
			{
				diagnostic = "managed XKB parser is not implemented";
				return null;
			}

			public Keys ModifierKeys {
				get { return Keys.None; }
			}

			public void SetModifiers (uint depressed, uint latched, uint locked, uint group)
			{
			}

			public WaylandKeyResult TranslateKey (uint evdevKey, bool pressed)
			{
				return new WaylandKeyResult ();
			}

			public void Dispose ()
			{
			}
		}

		sealed class LibXkbCommonKeyboardLayout : IWaylandKeyboardLayout {
			const int XkbContextNoFlags = 0;
			const int XkbKeymapFormatTextV1 = 1;
			const int XkbKeymapCompileNoFlags = 0;
			const string ModNameShift = "Shift";
			const string ModNameControl = "Control";
			const string ModNameAlt = "Mod1";

			IntPtr context;
			IntPtr keymap;
			IntPtr state;
			uint shiftIndex = UInt32.MaxValue;
			uint controlIndex = UInt32.MaxValue;
			uint altIndex = UInt32.MaxValue;
			Keys modifierKeys;

			LibXkbCommonKeyboardLayout (IntPtr context, IntPtr keymap, IntPtr state)
			{
				this.context = context;
				this.keymap = keymap;
				this.state = state;
				shiftIndex = xkb_keymap_mod_get_index (keymap, ModNameShift);
				controlIndex = xkb_keymap_mod_get_index (keymap, ModNameControl);
				altIndex = xkb_keymap_mod_get_index (keymap, ModNameAlt);
			}

			public static IWaylandKeyboardLayout TryCreate (WaylandKeymap waylandKeymap, out string diagnostic)
			{
				diagnostic = null;
				if (waylandKeymap.Format != WaylandProtocol.WlKeyboard.KeymapFormatXkbV1 || String.IsNullOrEmpty (waylandKeymap.Text)) {
					diagnostic = "unsupported Wayland keymap format";
					return null;
				}

				IntPtr context = IntPtr.Zero;
				IntPtr keymap = IntPtr.Zero;
				IntPtr state = IntPtr.Zero;
				try {
					context = xkb_context_new (XkbContextNoFlags);
					if (context == IntPtr.Zero) {
						diagnostic = "xkb_context_new failed";
						return null;
					}

					keymap = xkb_keymap_new_from_string (context, waylandKeymap.Text, XkbKeymapFormatTextV1, XkbKeymapCompileNoFlags);
					if (keymap == IntPtr.Zero) {
						diagnostic = "xkb_keymap_new_from_string failed";
						return null;
					}

					state = xkb_state_new (keymap);
					if (state == IntPtr.Zero) {
						diagnostic = "xkb_state_new failed";
						return null;
					}

					LibXkbCommonKeyboardLayout layout = new LibXkbCommonKeyboardLayout (context, keymap, state);
					context = keymap = state = IntPtr.Zero;
					return layout;
				} catch (DllNotFoundException e) {
					diagnostic = e.Message;
					return null;
				} catch (EntryPointNotFoundException e) {
					diagnostic = e.Message;
					return null;
				} finally {
					if (state != IntPtr.Zero)
						xkb_state_unref (state);
					if (keymap != IntPtr.Zero)
						xkb_keymap_unref (keymap);
					if (context != IntPtr.Zero)
						xkb_context_unref (context);
				}
			}

			public Keys ModifierKeys {
				get { return modifierKeys; }
			}

			public void SetModifiers (uint depressed, uint latched, uint locked, uint group)
			{
				xkb_state_update_mask (state, depressed, latched, locked, 0, 0, group);
				Keys keys = Keys.None;
				uint active = depressed | latched | locked;
				if (IsMaskActive (active, shiftIndex))
					keys |= Keys.Shift;
				if (IsMaskActive (active, controlIndex))
					keys |= Keys.Control;
				if (IsMaskActive (active, altIndex))
					keys |= Keys.Alt;
				modifierKeys = keys;
			}

			public WaylandKeyResult TranslateKey (uint evdevKey, bool pressed)
			{
				uint xkbKeycode = evdevKey + 8;
				WaylandKeyResult result = new WaylandKeyResult ();
				uint keysym = xkb_state_key_get_one_sym (state, xkbKeycode);
				result.KeyCode = MapKeysymToKeys (keysym);
				if (result.KeyCode == Keys.None)
					result.KeyCode = MapEvdevKey (evdevKey);
				if (pressed && (modifierKeys & Keys.Control) == 0)
					result.Text = GetUtf8 (state, xkbKeycode);
				return result;
			}

			public void Dispose ()
			{
				if (state != IntPtr.Zero) {
					xkb_state_unref (state);
					state = IntPtr.Zero;
				}
				if (keymap != IntPtr.Zero) {
					xkb_keymap_unref (keymap);
					keymap = IntPtr.Zero;
				}
				if (context != IntPtr.Zero) {
					xkb_context_unref (context);
					context = IntPtr.Zero;
				}
			}

			static bool IsMaskActive (uint mask, uint index)
			{
				return index != UInt32.MaxValue && index < 32 && (mask & (1u << (int) index)) != 0;
			}

			static string GetUtf8 (IntPtr state, uint xkbKeycode)
			{
				byte [] bytes = new byte [8];
				int actual = xkb_state_key_get_utf8 (state, xkbKeycode, bytes, (UIntPtr) bytes.Length);
				if (actual >= bytes.Length) {
					bytes = new byte [actual + 1];
					actual = xkb_state_key_get_utf8 (state, xkbKeycode, bytes, (UIntPtr) bytes.Length);
				}
				if (actual <= 0)
					return null;
				return Encoding.UTF8.GetString (bytes, 0, actual);
			}

			[DllImport ("libxkbcommon.so.0")]
			static extern IntPtr xkb_context_new (int flags);

			[DllImport ("libxkbcommon.so.0")]
			static extern void xkb_context_unref (IntPtr context);

			[DllImport ("libxkbcommon.so.0")]
			static extern IntPtr xkb_keymap_new_from_string (IntPtr context, string map, int format, int flags);

			[DllImport ("libxkbcommon.so.0")]
			static extern void xkb_keymap_unref (IntPtr keymap);

			[DllImport ("libxkbcommon.so.0")]
			static extern uint xkb_keymap_mod_get_index (IntPtr keymap, string name);

			[DllImport ("libxkbcommon.so.0")]
			static extern IntPtr xkb_state_new (IntPtr keymap);

			[DllImport ("libxkbcommon.so.0")]
			static extern void xkb_state_unref (IntPtr state);

			[DllImport ("libxkbcommon.so.0")]
			static extern int xkb_state_update_mask (IntPtr state, uint depressedMods, uint latchedMods, uint lockedMods, uint depressedLayout, uint latchedLayout, uint lockedLayout);

			[DllImport ("libxkbcommon.so.0")]
			static extern uint xkb_state_key_get_one_sym (IntPtr state, uint keycode);

			[DllImport ("libxkbcommon.so.0")]
			static extern int xkb_state_key_get_utf8 (IntPtr state, uint keycode, byte [] buffer, UIntPtr size);
		}

		readonly object queueLock = new object ();
		readonly Queue messageQueue = new Queue ();
		readonly Dictionary<IntPtr, WaylandWindow> windows = new Dictionary<IntPtr, WaylandWindow> ();
		readonly Dictionary<uint, WaylandWindow> waylandObjects = new Dictionary<uint, WaylandWindow> ();
		readonly Dictionary<uint, WaylandShmBuffer> waylandBuffers = new Dictionary<uint, WaylandShmBuffer> ();
		readonly Dictionary<uint, WaylandOutput> waylandOutputs = new Dictionary<uint, WaylandOutput> ();
		readonly Dictionary<IntPtr, WaylandCursor> cursors = new Dictionary<IntPtr, WaylandCursor> ();
		readonly List<IntPtr> zOrder = new List<IntPtr> ();
		readonly List<Timer> timers = new List<Timer> ();
		readonly Bitmap fallbackBitmap = new Bitmap (1, 1, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
		readonly HashSet<uint> evdevKeysDown = new HashSet<uint> ();
		readonly Dictionary<string, string> keyText = new Dictionary<string, string> ();
		readonly WaylandCaret caret = new WaylandCaret ();
		IWaylandKeyboardLayout keyboardLayout = new PhysicalUsKeyboardLayout ();

		WaylandConnection connection;
		WaylandRegistry registry;
		uint compositorId;
		uint subcompositorId;
		uint shmId;
		uint xdgWmBaseId;
		uint seatId;
		uint pointerId;
		uint keyboardId;
		uint cursorShapeManagerId;
		uint cursorShapeDeviceId;
		uint lastInputSerial;
		uint pointerEnterSerial;
		uint cursorSurfaceId;
		int nextHandle = 0x4000;
		int nextCursorHandle = 0x800000;
		IntPtr activeWindow = IntPtr.Zero;
		IntPtr focusWindow = IntPtr.Zero;
		IntPtr grabWindow = IntPtr.Zero;
		IntPtr overrideCursor = IntPtr.Zero;
		IntPtr renderedCursor = IntPtr.Zero;
		WaylandWindow pointerWindow;
		WaylandWindow keyboardWindow;
		Point pointerSurfacePosition;
		Point mousePosition;
		MouseButtons mouseButtons;
		Keys modifierKeys;
		uint keyboardModsDepressed;
		uint keyboardModsLatched;
		uint keyboardModsLocked;
		uint keyboardGroup;
		bool cursorVisible = true;
		bool cursorSurfaceCommitted;
		int renderedCursorScale;
		IntPtr lastClickWindow = IntPtr.Zero;
		Msg lastClickMessage;
		int lastClickX;
		int lastClickY;
		uint lastClickTime;
		bool themesEnabled;
		bool quitPosted;

		internal override event EventHandler Idle;

		WaylandConnection RequireConnection ()
		{
			if (connection == null)
				throw new InvalidOperationException ("The Wayland connection is closed.");
			return connection;
		}

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
			cursorShapeManagerId = connection.Bind (registry, "wp_cursor_shape_manager_v1", 1);
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
			WaylandConnection closingConnection = connection;
			connection = null;

			foreach (WaylandWindow window in windows.Values)
				window.Dispose ();
			foreach (WaylandShmBuffer buffer in waylandBuffers.Values)
				buffer.Dispose ();
			if (closingConnection != null) {
				if (cursorShapeDeviceId != 0)
					closingConnection.SendRequest (cursorShapeDeviceId, WaylandProtocol.WpCursorShapeDeviceV1.Destroy, null);
				if (cursorShapeManagerId != 0)
					closingConnection.SendRequest (cursorShapeManagerId, WaylandProtocol.WpCursorShapeManagerV1.Destroy, null);
				if (cursorSurfaceId != 0)
					closingConnection.SendRequest (cursorSurfaceId, WaylandProtocol.WlSurface.Destroy, null);
			}
			foreach (WaylandCursor cursor in cursors.Values) {
				if (cursor.Bitmap != null)
					cursor.Bitmap.Dispose ();
			}
			fallbackBitmap.Dispose ();
			if (caret.Timer != null)
				caret.Timer.Dispose ();
			caret.Timer = null;
			caret.Hwnd = IntPtr.Zero;
			windows.Clear ();
			waylandObjects.Clear ();
			waylandBuffers.Clear ();
			waylandOutputs.Clear ();
			cursors.Clear ();
			evdevKeysDown.Clear ();
			keyText.Clear ();
			keyboardLayout.Dispose ();
			keyboardLayout = new PhysicalUsKeyboardLayout ();
			modifierKeys = Keys.None;
			keyboardModsDepressed = 0;
			keyboardModsLatched = 0;
			keyboardModsLocked = 0;
			keyboardGroup = 0;
			cursorSurfaceId = 0;
			cursorShapeDeviceId = 0;
			cursorShapeManagerId = 0;
			cursorSurfaceCommitted = false;
			renderedCursor = IntPtr.Zero;

			if (closingConnection != null)
				closingConnection.Dispose ();
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

			WaylandConnection liveConnection = RequireConnection ();
			if (state == FormWindowState.Maximized) {
				liveConnection.SendRequest (window.XdgToplevelId, WaylandProtocol.XdgToplevel.SetMaximized, null);
			} else if (state == FormWindowState.Minimized) {
				liveConnection.SendRequest (window.XdgToplevelId, WaylandProtocol.XdgToplevel.SetMinimized, null);
			} else {
				liveConnection.SendRequest (window.XdgToplevelId, WaylandProtocol.XdgToplevel.UnsetMaximized, null);
			}
		}

		internal override void SetWindowMinMax (IntPtr handle, Rectangle maximized, Size min, Size max)
		{
			WaylandWindow window;
			if (!windows.TryGetValue (handle, out window) || window.XdgToplevelId == 0)
				return;

			WaylandConnection liveConnection = RequireConnection ();
			liveConnection.SendRequest (window.XdgToplevelId, WaylandProtocol.XdgToplevel.SetMinSize, delegate (WaylandRequestBuilder b) {
				b.WriteInt32 (min.Width);
				b.WriteInt32 (min.Height);
			});
			liveConnection.SendRequest (window.XdgToplevelId, WaylandProtocol.XdgToplevel.SetMaxSize, delegate (WaylandRequestBuilder b) {
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
				WaylandConnection liveConnection = RequireConnection ();
				liveConnection.SendRequest (window.XdgToplevelId, WaylandProtocol.XdgToplevel.SetTitle, delegate (WaylandRequestBuilder b) {
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
				if (!ShouldDeferUnownedPopup (window)) {
					EnsureNativeWindow (window);
					if (IsSubsurfaceWindow (window))
						Invalidate (handle, Rectangle.Empty, false);
					else if (IsPopupWindow (window))
						InvalidateWindowTree (window);
					if (activate && !IsSubsurfaceWindow (window) && !IsPopupWindow (window))
						Activate (handle);
				}
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
				InvalidateWindowTree (window);
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
			bool movedOrResized = window.Hwnd.X != x || window.Hwnd.Y != y || oldWidth != width || oldHeight != height;
			bool popupRolePositionChanged = window.XdgPopupId != 0 &&
				movedOrResized;

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
				InvalidateWindowTree (window);
			}
			UpdateSubsurfacePosition (window);
			// SetBoundsCore expects SetWindowPos to make Control.bounds current
			// before returning.  PropertyGridView reuses one dropdown Form and
			// reads Location immediately after assigning it; queueing this
			// message leaves that read seeing the previous popup position.
			SendMessage (handle, Msg.WM_WINDOWPOSCHANGED, IntPtr.Zero, IntPtr.Zero);

			// A Wayland subsurface's visible extent is the last committed buffer
			// size, not Mono's Hwnd bounds.  If a child grows without repainting,
			// the compositor can keep showing the old narrower buffer even though
			// WinForms layout has already changed the logical client size.
			if (window.Hwnd.visible && IsSubsurfaceWindow (window) && movedOrResized)
				Invalidate (handle, Rectangle.Empty, false);
			else if (window.Hwnd.visible && (window.Hwnd.Width != oldWidth || window.Hwnd.Height != oldHeight))
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
			string text;
			string key = GetKeyTextKey (msg);
			if (!keyText.TryGetValue (key, out text))
				return true;

			keyText.Remove (key);
			// Mono's Application loop calls TranslateMessage between its
			// keyboard-capture/preprocessing checks and DispatchMessage.  Post
			// WM_CHAR here, not directly from wl_keyboard.key, so shortcut and
			// dialog-key filtering sees the same message order as the other
			// native drivers.
			for (int i = 0; i < text.Length; i++)
				PostInputMessage (msg.hwnd, msg.message == Msg.WM_SYSKEYDOWN ? Msg.WM_SYSCHAR : Msg.WM_CHAR, (IntPtr) text [i], msg.lParam, msg.time);
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
					InvalidateWindowTree (window);
				}
			} else if (isPopup && window.Hwnd.visible && window.SurfaceId == 0) {
				// ComboBox.ComboListBox calls Show before XplatUI.SetOwner.
				// Wayland roles are immutable, so SetVisible deliberately
				// deferred creating an unowned non-Form WS_POPUP surface.  Now
				// that Mono has supplied the owner, create it directly as the
				// xdg_popup it should have been from the start.
				EnsureNativeWindow (window);
				InvalidateWindowTree (window);
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
			WaylandWindow window;
			if (windows.TryGetValue (hwnd, out window)) {
				window.Hwnd.cursor = cursor;
				if (pointerWindow == window)
					ApplyCursor (window);
			}
		}

		internal override void ShowCursor (bool show)
		{
			cursorVisible = show;
			if (pointerWindow != null)
				ApplyCursor (pointerWindow);
		}

		internal override void OverrideCursor (IntPtr cursor)
		{
			overrideCursor = cursor;
			if (pointerWindow != null)
				ApplyCursor (pointerWindow);
		}

		internal override IntPtr DefineCursor (Bitmap bitmap, Bitmap mask, Color cursorPixel, Color maskPixel, int xHotSpot, int yHotSpot)
		{
			if (bitmap == null)
				return IntPtr.Zero;

			IntPtr handle = AllocateCursorHandle ();
			cursors [handle] = new WaylandCursor {
				Handle = handle,
				Bitmap = CreateCustomCursorBitmap (bitmap, mask, cursorPixel, maskPixel),
				HotspotX = Math.Max (0, xHotSpot),
				HotspotY = Math.Max (0, yHotSpot),
			};
			return handle;
		}

		internal override IntPtr DefineStdCursor (StdCursor id)
		{
			IntPtr handle = StandardCursorHandle (id);
			if (!cursors.ContainsKey (handle)) {
				Point hotspot;
				cursors [handle] = new WaylandCursor {
					Handle = handle,
					Standard = true,
					StandardId = id,
					Bitmap = CreateStandardCursorBitmap (id, 1, out hotspot),
					HotspotX = hotspot.X,
					HotspotY = hotspot.Y,
				};
			}
			return handle;
		}

		internal override Bitmap DefineStdCursorBitmap (StdCursor id)
		{
			Point hotspot;
			return CreateStandardCursorBitmap (id, 1, out hotspot);
		}

		internal override void DestroyCursor (IntPtr cursor)
		{
			WaylandCursor definition;
			if (!cursors.TryGetValue (cursor, out definition) || definition.Standard)
				return;

			cursors.Remove (cursor);
			if (definition.Bitmap != null)
				definition.Bitmap.Dispose ();
			if (renderedCursor == cursor) {
				renderedCursor = IntPtr.Zero;
				cursorSurfaceCommitted = false;
				if (pointerWindow != null)
					ApplyCursor (pointerWindow);
			}
		}

		internal override void GetCursorInfo (IntPtr cursor, out int width, out int height, out int hotspotX, out int hotspotY)
		{
			WaylandCursor definition = GetCursorDefinition (cursor);
			width = definition.Bitmap.Width;
			height = definition.Bitmap.Height;
			hotspotX = definition.HotspotX;
			hotspotY = definition.HotspotY;
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
			// StartLoop/EndLoop bracket nested WinForms loops such as
			// PropertyGrid drop-down editors.  Ending one of those loops must not
			// post WM_QUIT; only Application.Exit/ExitThread should terminate the
			// application message loop.
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
			if (window.SurfaceId != 0)
				return;

			WaylandConnection liveConnection = RequireConnection ();

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

				window.SurfaceId = liveConnection.AllocateId ();
				window.SubsurfaceId = liveConnection.AllocateId ();
				// Parent and child must share one logical coordinate system.
				// Scaling only changes buffer density, never child bounds.
				window.BufferScale = parent.BufferScale;

				liveConnection.SendRequest (compositorId, WaylandProtocol.WlCompositor.CreateSurface, delegate (WaylandRequestBuilder b) {
					b.WriteNewId (window.SurfaceId);
				});
				liveConnection.SendRequest (subcompositorId, WaylandProtocol.WlSubcompositor.GetSubsurface, delegate (WaylandRequestBuilder b) {
					b.WriteNewId (window.SubsurfaceId);
					b.WriteObject (window.SurfaceId);
					b.WriteObject (parent.SurfaceId);
				});
				liveConnection.SendRequest (window.SurfaceId, WaylandProtocol.WlSurface.SetBufferScale, delegate (WaylandRequestBuilder b) {
					b.WriteInt32 (window.BufferScale);
				});
				UpdateSubsurfacePosition (window);
				// Mono paints HWNDs independently.  Desync lets a child commit its
				// new buffer without waiting for an explicit parent commit.
				liveConnection.SendRequest (window.SubsurfaceId, WaylandProtocol.WlSubsurface.SetDesync, null);
				ApplySubsurfaceZOrder (window, IntPtr.Zero, true, false);
				liveConnection.SendRequest (window.SurfaceId, WaylandProtocol.WlSurface.Commit, null);

				waylandObjects [window.SurfaceId] = window;
				waylandObjects [window.SubsurfaceId] = window;
				return;
			}

			if (IsPopupWindow (window)) {
				CreatePopupNativeWindow (window);
				return;
			}

			window.SurfaceId = liveConnection.AllocateId ();
			window.XdgSurfaceId = liveConnection.AllocateId ();
			window.XdgToplevelId = liveConnection.AllocateId ();

			liveConnection.SendRequest (compositorId, WaylandProtocol.WlCompositor.CreateSurface, delegate (WaylandRequestBuilder b) {
				b.WriteNewId (window.SurfaceId);
			});
			liveConnection.SendRequest (xdgWmBaseId, WaylandProtocol.XdgWmBase.GetXdgSurface, delegate (WaylandRequestBuilder b) {
				b.WriteNewId (window.XdgSurfaceId);
				b.WriteObject (window.SurfaceId);
			});
			liveConnection.SendRequest (window.XdgSurfaceId, WaylandProtocol.XdgSurface.GetToplevel, delegate (WaylandRequestBuilder b) {
				b.WriteNewId (window.XdgToplevelId);
			});
			liveConnection.SendRequest (window.XdgToplevelId, WaylandProtocol.XdgToplevel.SetTitle, delegate (WaylandRequestBuilder b) {
				b.WriteString (window.Text);
			});
			liveConnection.SendRequest (window.XdgToplevelId, WaylandProtocol.XdgToplevel.SetAppId, delegate (WaylandRequestBuilder b) {
				b.WriteString ("mono-winforms");
			});
			liveConnection.SendRequest (window.SurfaceId, WaylandProtocol.WlSurface.SetBufferScale, delegate (WaylandRequestBuilder b) {
				b.WriteInt32 (window.BufferScale);
			});
			liveConnection.SendRequest (window.SurfaceId, WaylandProtocol.WlSurface.Commit, null);

			waylandObjects [window.SurfaceId] = window;
			waylandObjects [window.XdgSurfaceId] = window;
			waylandObjects [window.XdgToplevelId] = window;
		}

		void CreatePopupNativeWindow (WaylandWindow window)
		{
			WaylandConnection liveConnection = RequireConnection ();
			WaylandWindow parent = GetPopupParentWindow (window);
			if (parent == null)
				return;

			EnsureNativeWindow (parent);
			if (parent.XdgSurfaceId == 0)
				return;

			window.SurfaceId = liveConnection.AllocateId ();
			uint positionerId = liveConnection.AllocateId ();
			window.BufferScale = parent.BufferScale;

			Point parentScreen = GetWindowScreenLocation (parent);
			Point popupScreen = GetWindowScreenLocation (window);
			int relativeX = popupScreen.X - parentScreen.X;
			int relativeY = popupScreen.Y - parentScreen.Y;
			int width = Math.Max (1, window.Hwnd.Width);
			int height = Math.Max (1, window.Hwnd.Height);

			liveConnection.SendRequest (compositorId, WaylandProtocol.WlCompositor.CreateSurface, delegate (WaylandRequestBuilder b) {
				b.WriteNewId (window.SurfaceId);
			});
			// xdg_popup gets all placement state from an xdg_positioner.
			// Build that object before assigning the popup role, and keep each
			// new_id allocation next to the request that introduces it on the wire.
			liveConnection.SendRequest (xdgWmBaseId, WaylandProtocol.XdgWmBase.CreatePositioner, delegate (WaylandRequestBuilder b) {
				b.WriteNewId (positionerId);
			});
			liveConnection.SendRequest (positionerId, WaylandProtocol.XdgPositioner.SetSize, delegate (WaylandRequestBuilder b) {
				b.WriteInt32 (width);
				b.WriteInt32 (height);
			});
			liveConnection.SendRequest (positionerId, WaylandProtocol.XdgPositioner.SetAnchorRect, delegate (WaylandRequestBuilder b) {
				b.WriteInt32 (relativeX);
				b.WriteInt32 (relativeY);
				b.WriteInt32 (1);
				b.WriteInt32 (1);
			});
			liveConnection.SendRequest (positionerId, WaylandProtocol.XdgPositioner.SetAnchor, delegate (WaylandRequestBuilder b) {
				b.WriteUInt32 (WaylandProtocol.XdgPositioner.AnchorTopLeft);
			});
			liveConnection.SendRequest (positionerId, WaylandProtocol.XdgPositioner.SetGravity, delegate (WaylandRequestBuilder b) {
				b.WriteUInt32 (WaylandProtocol.XdgPositioner.GravityBottomRight);
			});
			liveConnection.SendRequest (positionerId, WaylandProtocol.XdgPositioner.SetConstraintAdjustment, delegate (WaylandRequestBuilder b) {
				b.WriteUInt32 (WaylandProtocol.XdgPositioner.ConstraintAdjustmentSlideX |
					WaylandProtocol.XdgPositioner.ConstraintAdjustmentSlideY |
					WaylandProtocol.XdgPositioner.ConstraintAdjustmentFlipX |
					WaylandProtocol.XdgPositioner.ConstraintAdjustmentFlipY |
					WaylandProtocol.XdgPositioner.ConstraintAdjustmentResizeX |
					WaylandProtocol.XdgPositioner.ConstraintAdjustmentResizeY);
			});
			window.XdgSurfaceId = liveConnection.AllocateId ();
			liveConnection.SendRequest (xdgWmBaseId, WaylandProtocol.XdgWmBase.GetXdgSurface, delegate (WaylandRequestBuilder b) {
				b.WriteNewId (window.XdgSurfaceId);
				b.WriteObject (window.SurfaceId);
			});
			// Owned WinForms popup HWNDs are not independent application
			// windows.  xdg_popup gives the compositor the same relationship
			// that Mono expresses through Hwnd.owner, including outside-click
			// dismissal for grabbed popup menus and combo lists.
			window.XdgPopupId = liveConnection.AllocateId ();
			liveConnection.SendRequest (window.XdgSurfaceId, WaylandProtocol.XdgSurface.GetPopup, delegate (WaylandRequestBuilder b) {
				b.WriteNewId (window.XdgPopupId);
				b.WriteObject (parent.XdgSurfaceId);
				b.WriteObject (positionerId);
			});
			liveConnection.SendRequest (positionerId, WaylandProtocol.XdgPositioner.Destroy, null);
			if (seatId != 0 && lastInputSerial != 0) {
				// The xdg_popup grab is tied to a user-input serial by design;
				// without that serial the compositor must reject the grab.  The
				// popup still maps, but outside-click dismissal is compositor
				// managed only for user-triggered popups.
				liveConnection.SendRequest (window.XdgPopupId, WaylandProtocol.XdgPopup.Grab, delegate (WaylandRequestBuilder b) {
					b.WriteObject (seatId);
					b.WriteUInt32 (lastInputSerial);
				});
			}
			liveConnection.SendRequest (window.SurfaceId, WaylandProtocol.WlSurface.SetBufferScale, delegate (WaylandRequestBuilder b) {
				b.WriteInt32 (window.BufferScale);
			});
			liveConnection.SendRequest (window.SurfaceId, WaylandProtocol.WlSurface.Commit, null);

			waylandObjects [window.SurfaceId] = window;
			waylandObjects [window.XdgSurfaceId] = window;
			waylandObjects [window.XdgPopupId] = window;
		}

		void CommitWindowBuffer (WaylandWindow window)
		{
			if (window.SurfaceId == 0)
				return;
			if (window.XdgSurfaceId != 0 && !window.XdgConfigured)
				return;
			WaylandConnection liveConnection = RequireConnection ();
			Rectangle sourceLogical = Rectangle.Empty;
			Point surfacePosition = Point.Empty;
			bool clippedSubsurface = false;

			if (window.SubsurfaceId != 0) {
				if (!TryGetSubsurfaceGeometry (window, out surfacePosition, out sourceLogical)) {
					DetachSurfaceBuffer (window);
					return;
				}

				SendSubsurfacePosition (window, surfacePosition);
				Rectangle full = new Rectangle (0, 0, Math.Max (1, window.Hwnd.ClientRect.Width), Math.Max (1, window.Hwnd.ClientRect.Height));
				clippedSubsurface = sourceLogical != full;
			}

			window.EnsureBackBuffer ();
			Bitmap commitBitmap = window.BackBuffer;
			Bitmap caretBitmap = null;
			Bitmap clippedBitmap = null;

			try {
				if (caret.Hwnd == window.Hwnd.Handle && caret.Visible && caret.On) {
					// The caret is driver-owned state, not part of the control's
					// painting.  Draw it onto a temporary commit bitmap so blink
					// toggles never corrupt the pristine WinForms backing store.
					caretBitmap = new Bitmap (window.BackBuffer);
					DrawCaretOverlay (window, caretBitmap);
					commitBitmap = caretBitmap;
				}

				if (clippedSubsurface) {
					// Wayland subsurfaces are not clipped by their parent
					// surface.  Native WinForms child HWNDs are, so commit only
					// the parent-visible source rectangle and move the subsurface
					// to that rectangle's screen position.
					clippedBitmap = CreateClippedCommitBitmap (commitBitmap, sourceLogical, window.BufferScale);
					commitBitmap = clippedBitmap;
				}

				WaylandShmBuffer buffer = WaylandShmBuffer.CreateFromBitmap (liveConnection, shmId, commitBitmap, window.BufferScale);
				window.Buffers.Add (buffer);
				waylandBuffers [buffer.BufferId] = buffer;

				// wl_surface.damage_buffer is in physical buffer pixels.  The
				// buffer scale tells the compositor how those pixels map back to
				// the surface's logical size.
				liveConnection.SendRequest (window.SurfaceId, WaylandProtocol.WlSurface.SetBufferScale, delegate (WaylandRequestBuilder b) {
					b.WriteInt32 (buffer.Scale);
				});
				liveConnection.SendRequest (window.SurfaceId, WaylandProtocol.WlSurface.Attach, delegate (WaylandRequestBuilder b) {
					b.WriteObject (buffer.BufferId);
					b.WriteInt32 (0);
					b.WriteInt32 (0);
				});
				liveConnection.SendRequest (window.SurfaceId, WaylandProtocol.WlSurface.DamageBuffer, delegate (WaylandRequestBuilder b) {
					b.WriteInt32 (0);
					b.WriteInt32 (0);
					b.WriteInt32 (buffer.BufferWidth);
					b.WriteInt32 (buffer.BufferHeight);
				});
				liveConnection.SendRequest (window.SurfaceId, WaylandProtocol.WlSurface.Commit, null);
				window.BufferAttached = true;
			} finally {
				if (clippedBitmap != null)
					clippedBitmap.Dispose ();
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

		static Bitmap CreateClippedCommitBitmap (Bitmap source, Rectangle logicalSource, int scale)
		{
			scale = Math.Max (1, scale);
			Rectangle physicalSource = new Rectangle (logicalSource.X * scale, logicalSource.Y * scale,
				Math.Max (1, logicalSource.Width * scale), Math.Max (1, logicalSource.Height * scale));
			Bitmap bitmap = new Bitmap (physicalSource.Width, physicalSource.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
			bitmap.SetResolution (source.HorizontalResolution, source.VerticalResolution);

			using (Graphics graphics = Graphics.FromImage (bitmap)) {
				graphics.CompositingMode = CompositingMode.SourceCopy;
				graphics.DrawImage (source, new Rectangle (0, 0, bitmap.Width, bitmap.Height), physicalSource, GraphicsUnit.Pixel);
			}

			return bitmap;
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

		void ApplyCursor (WaylandWindow window)
		{
			if (pointerId == 0 || pointerEnterSerial == 0)
				return;
			WaylandConnection liveConnection = RequireConnection ();

			if (!cursorVisible) {
				liveConnection.SendRequest (pointerId, WaylandProtocol.WlPointer.SetCursor, delegate (WaylandRequestBuilder b) {
					b.WriteUInt32 (pointerEnterSerial);
					b.WriteObject (0);
					b.WriteInt32 (0);
					b.WriteInt32 (0);
				});
				return;
			}

			IntPtr handle = overrideCursor != IntPtr.Zero ? overrideCursor : window.Hwnd.cursor;
			if (handle == IntPtr.Zero)
				handle = DefineStdCursor (StdCursor.Default);

			WaylandCursor cursor = GetCursorDefinition (handle);
			uint shape;
			if (TryGetCursorShape (cursor, out shape)) {
				EnsureCursorShapeDevice ();
					if (cursorShapeDeviceId != 0) {
					// cursor-shape-v1 lets standard WinForms cursors use the
					// compositor/theme's cursor artwork.  Keep the SHM cursor
					// path below for custom cursors and older compositors.
						liveConnection.SendRequest (cursorShapeDeviceId, WaylandProtocol.WpCursorShapeDeviceV1.SetShape, delegate (WaylandRequestBuilder b) {
							b.WriteUInt32 (pointerEnterSerial);
							b.WriteUInt32 (shape);
						});
					return;
				}
			}

			int scale = Math.Max (1, GetTargetScale (window));
			EnsureCursorSurface ();
			RenderCursorSurface (cursor, scale);

			// Wayland cursors are not window properties.  The compositor asks
			// for a cursor surface per pointer-enter serial, so every enter and
			// every managed SetCursor needs to update wl_pointer.set_cursor.
			liveConnection.SendRequest (pointerId, WaylandProtocol.WlPointer.SetCursor, delegate (WaylandRequestBuilder b) {
				b.WriteUInt32 (pointerEnterSerial);
				b.WriteObject (cursorSurfaceId);
				b.WriteInt32 (cursor.HotspotX);
				b.WriteInt32 (cursor.HotspotY);
			});
		}

		void EnsureCursorSurface ()
		{
			if (cursorSurfaceId != 0)
				return;

			WaylandConnection liveConnection = RequireConnection ();
			cursorSurfaceId = liveConnection.AllocateId ();
			liveConnection.SendRequest (compositorId, WaylandProtocol.WlCompositor.CreateSurface, delegate (WaylandRequestBuilder b) {
				b.WriteNewId (cursorSurfaceId);
			});
			cursorSurfaceCommitted = false;
		}

		void EnsureCursorShapeDevice ()
		{
			if (cursorShapeManagerId == 0 || pointerId == 0 || cursorShapeDeviceId != 0)
				return;

			WaylandConnection liveConnection = RequireConnection ();
			cursorShapeDeviceId = liveConnection.AllocateId ();
			liveConnection.SendRequest (cursorShapeManagerId, WaylandProtocol.WpCursorShapeManagerV1.GetPointer, delegate (WaylandRequestBuilder b) {
				b.WriteNewId (cursorShapeDeviceId);
				b.WriteObject (pointerId);
			});
		}

		bool TryGetCursorShape (WaylandCursor cursor, out uint shape)
		{
			shape = 0;
			if (!cursor.Standard)
				return false;

			switch (cursor.StandardId) {
			case StdCursor.Default:
			case StdCursor.Arrow:
				shape = WaylandProtocol.WpCursorShapeDeviceV1.Default;
				return true;
			case StdCursor.AppStarting:
				shape = WaylandProtocol.WpCursorShapeDeviceV1.Progress;
				return true;
			case StdCursor.Cross:
				shape = WaylandProtocol.WpCursorShapeDeviceV1.Crosshair;
				return true;
			case StdCursor.Hand:
				shape = WaylandProtocol.WpCursorShapeDeviceV1.Pointer;
				return true;
			case StdCursor.Help:
				shape = WaylandProtocol.WpCursorShapeDeviceV1.Help;
				return true;
			case StdCursor.IBeam:
				shape = WaylandProtocol.WpCursorShapeDeviceV1.Text;
				return true;
			case StdCursor.No:
				shape = WaylandProtocol.WpCursorShapeDeviceV1.NotAllowed;
				return true;
			case StdCursor.NoMove2D:
			case StdCursor.SizeAll:
				shape = WaylandProtocol.WpCursorShapeDeviceV1.AllScroll;
				return true;
			case StdCursor.NoMoveHoriz:
			case StdCursor.HSplit:
			case StdCursor.SizeWE:
				shape = WaylandProtocol.WpCursorShapeDeviceV1.EwResize;
				return true;
			case StdCursor.NoMoveVert:
			case StdCursor.VSplit:
			case StdCursor.SizeNS:
				shape = WaylandProtocol.WpCursorShapeDeviceV1.NsResize;
				return true;
			case StdCursor.SizeNESW:
				shape = WaylandProtocol.WpCursorShapeDeviceV1.NeswResize;
				return true;
			case StdCursor.SizeNWSE:
				shape = WaylandProtocol.WpCursorShapeDeviceV1.NwseResize;
				return true;
			case StdCursor.PanEast:
				shape = WaylandProtocol.WpCursorShapeDeviceV1.EResize;
				return true;
			case StdCursor.PanNE:
				shape = WaylandProtocol.WpCursorShapeDeviceV1.NeResize;
				return true;
			case StdCursor.PanNorth:
				shape = WaylandProtocol.WpCursorShapeDeviceV1.NResize;
				return true;
			case StdCursor.PanNW:
				shape = WaylandProtocol.WpCursorShapeDeviceV1.NwResize;
				return true;
			case StdCursor.PanSE:
				shape = WaylandProtocol.WpCursorShapeDeviceV1.SeResize;
				return true;
			case StdCursor.PanSouth:
				shape = WaylandProtocol.WpCursorShapeDeviceV1.SResize;
				return true;
			case StdCursor.PanSW:
				shape = WaylandProtocol.WpCursorShapeDeviceV1.SwResize;
				return true;
			case StdCursor.PanWest:
				shape = WaylandProtocol.WpCursorShapeDeviceV1.WResize;
				return true;
			case StdCursor.UpArrow:
				shape = WaylandProtocol.WpCursorShapeDeviceV1.NResize;
				return true;
			case StdCursor.WaitCursor:
				shape = WaylandProtocol.WpCursorShapeDeviceV1.Wait;
				return true;
			default:
				return false;
			}
		}

		void RenderCursorSurface (WaylandCursor cursor, int scale)
		{
			if (cursorSurfaceCommitted && renderedCursor == cursor.Handle && renderedCursorScale == scale)
				return;

			WaylandConnection liveConnection = RequireConnection ();
			using (Bitmap bitmap = RenderCursorBitmap (cursor, scale)) {
				WaylandShmBuffer buffer = WaylandShmBuffer.CreateFromBitmap (liveConnection, shmId, bitmap, scale);
				waylandBuffers [buffer.BufferId] = buffer;

				liveConnection.SendRequest (cursorSurfaceId, WaylandProtocol.WlSurface.SetBufferScale, delegate (WaylandRequestBuilder b) {
					b.WriteInt32 (scale);
				});
				liveConnection.SendRequest (cursorSurfaceId, WaylandProtocol.WlSurface.Attach, delegate (WaylandRequestBuilder b) {
					b.WriteObject (buffer.BufferId);
					b.WriteInt32 (0);
					b.WriteInt32 (0);
				});
				liveConnection.SendRequest (cursorSurfaceId, WaylandProtocol.WlSurface.DamageBuffer, delegate (WaylandRequestBuilder b) {
					b.WriteInt32 (0);
					b.WriteInt32 (0);
					b.WriteInt32 (buffer.BufferWidth);
					b.WriteInt32 (buffer.BufferHeight);
				});
				liveConnection.SendRequest (cursorSurfaceId, WaylandProtocol.WlSurface.Commit, null);
			}

			renderedCursor = cursor.Handle;
			renderedCursorScale = scale;
			cursorSurfaceCommitted = true;
		}

		Bitmap RenderCursorBitmap (WaylandCursor cursor, int scale)
		{
			if (cursor.Standard) {
				Point unused;
				return CreateStandardCursorBitmap (cursor.StandardId, scale, out unused);
			}

			Bitmap source = cursor.Bitmap;
			int width = Math.Max (1, source.Width * scale);
			int height = Math.Max (1, source.Height * scale);
			Bitmap scaled = new Bitmap (width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
			scaled.SetResolution (96.0f * scale, 96.0f * scale);
			using (Graphics graphics = Graphics.FromImage (scaled)) {
				graphics.Clear (Color.Transparent);
				graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
				graphics.PixelOffsetMode = PixelOffsetMode.Half;
				graphics.DrawImage (source, new Rectangle (0, 0, width, height), new Rectangle (0, 0, source.Width, source.Height), GraphicsUnit.Pixel);
			}
			return scaled;
		}

		WaylandCursor GetCursorDefinition (IntPtr handle)
		{
			WaylandCursor cursor;
			if (handle != IntPtr.Zero && cursors.TryGetValue (handle, out cursor))
				return cursor;
			return cursors [DefineStdCursor (StdCursor.Default)];
		}

		IntPtr AllocateCursorHandle ()
		{
			return (IntPtr) Interlocked.Increment (ref nextCursorHandle);
		}

		static IntPtr StandardCursorHandle (StdCursor id)
		{
			return (IntPtr) (0x100000 + (int) id);
		}

		static Bitmap CreateCustomCursorBitmap (Bitmap bitmap, Bitmap mask, Color cursorPixel, Color maskPixel)
		{
			if (mask == null)
				return new Bitmap (bitmap);

			Bitmap result = new Bitmap (bitmap.Width, bitmap.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
			for (int y = 0; y < result.Height; y++) {
				for (int x = 0; x < result.Width; x++) {
					Color source = bitmap.GetPixel (x, y);
					Color maskPixelAtPoint = x < mask.Width && y < mask.Height ? mask.GetPixel (x, y) : Color.Empty;
					bool andBit = source.ToArgb () == cursorPixel.ToArgb ();
					bool xorBit = maskPixelAtPoint.ToArgb () == maskPixel.ToArgb ();

					if (!andBit && !xorBit)
						result.SetPixel (x, y, Color.Black);
					else if (andBit && !xorBit)
						result.SetPixel (x, y, Color.White);
					else
						result.SetPixel (x, y, Color.Transparent);
				}
			}
			return result;
		}

		static Bitmap CreateStandardCursorBitmap (StdCursor id, int scale, out Point hotspot)
		{
			hotspot = GetStandardCursorHotspot (id);
			scale = Math.Max (1, scale);
			Size logicalSize = GetStandardCursorSize (id);
			Bitmap bitmap = new Bitmap (logicalSize.Width * scale, logicalSize.Height * scale, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
			bitmap.SetResolution (96.0f * scale, 96.0f * scale);
			using (Graphics graphics = Graphics.FromImage (bitmap)) {
				graphics.Clear (Color.Transparent);
				graphics.SmoothingMode = SmoothingMode.AntiAlias;
				graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
				// Core Wayland does not prescribe a fallback cursor size.  For
				// standard cursors Mono gives us only a StdCursor id, so this
				// backend owns the intrinsic logical size for each fallback shape;
				// custom cursors use the exact bitmap size passed to DefineCursor.
				graphics.ScaleTransform (logicalSize.Width * scale / 32.0f, logicalSize.Height * scale / 32.0f);

				switch (id) {
				case StdCursor.Cross:
					DrawCrossCursor (graphics);
					break;
				case StdCursor.IBeam:
					DrawIBeamCursor (graphics);
					break;
				case StdCursor.Hand:
					DrawHandCursor (graphics);
					break;
				case StdCursor.No:
					DrawNoCursor (graphics);
					break;
				case StdCursor.SizeAll:
				case StdCursor.NoMove2D:
					DrawSizeAllCursor (graphics);
					break;
				case StdCursor.SizeWE:
				case StdCursor.HSplit:
				case StdCursor.NoMoveHoriz:
				case StdCursor.PanEast:
				case StdCursor.PanWest:
					DrawHorizontalResizeCursor (graphics);
					break;
				case StdCursor.SizeNS:
				case StdCursor.VSplit:
				case StdCursor.NoMoveVert:
				case StdCursor.PanNorth:
				case StdCursor.PanSouth:
					DrawVerticalResizeCursor (graphics);
					break;
				case StdCursor.SizeNESW:
				case StdCursor.PanNE:
				case StdCursor.PanSW:
					DrawDiagonalResizeCursor (graphics, true);
					break;
				case StdCursor.SizeNWSE:
				case StdCursor.PanNW:
				case StdCursor.PanSE:
					DrawDiagonalResizeCursor (graphics, false);
					break;
				case StdCursor.WaitCursor:
					DrawWaitCursor (graphics);
					break;
				case StdCursor.Help:
					DrawArrowCursor (graphics);
					DrawQuestionMark (graphics);
					break;
				case StdCursor.AppStarting:
					DrawArrowCursor (graphics);
					DrawSmallWaitCursor (graphics);
					break;
				case StdCursor.UpArrow:
					DrawUpArrowCursor (graphics);
					break;
				default:
					DrawArrowCursor (graphics);
					break;
				}
			}
			return bitmap;
		}

		static Size GetStandardCursorSize (StdCursor id)
		{
			switch (id) {
			case StdCursor.IBeam:
				return new Size (18, 24);
			case StdCursor.Cross:
				return new Size (25, 25);
			case StdCursor.WaitCursor:
			case StdCursor.No:
				return new Size (26, 26);
			case StdCursor.Hand:
				return new Size (24, 28);
			case StdCursor.SizeAll:
			case StdCursor.SizeNESW:
			case StdCursor.SizeNS:
			case StdCursor.SizeNWSE:
			case StdCursor.SizeWE:
			case StdCursor.HSplit:
			case StdCursor.VSplit:
				return new Size (28, 28);
			default:
				return new Size (32, 32);
			}
		}

		static Point GetStandardCursorHotspot (StdCursor id)
		{
			switch (id) {
			case StdCursor.Cross:
				return new Point (12, 12);
			case StdCursor.IBeam:
				return new Point (9, 12);
			case StdCursor.No:
			case StdCursor.WaitCursor:
				return new Point (13, 13);
			case StdCursor.NoMove2D:
			case StdCursor.NoMoveHoriz:
			case StdCursor.NoMoveVert:
			case StdCursor.PanEast:
			case StdCursor.PanNE:
			case StdCursor.PanNorth:
			case StdCursor.PanNW:
			case StdCursor.PanSE:
			case StdCursor.PanSouth:
			case StdCursor.PanSW:
			case StdCursor.PanWest:
			case StdCursor.SizeAll:
			case StdCursor.SizeNESW:
			case StdCursor.SizeNS:
			case StdCursor.SizeNWSE:
			case StdCursor.SizeWE:
			case StdCursor.HSplit:
			case StdCursor.VSplit:
				return new Point (14, 14);
			case StdCursor.Hand:
				return new Point (8, 6);
			case StdCursor.UpArrow:
				return new Point (16, 1);
			default:
				return new Point (2, 2);
			}
		}

		static void DrawArrowCursor (Graphics graphics)
		{
			FillOutlinedPolygon (graphics, new [] {
				new PointF (2, 2),
				new PointF (2, 25),
				new PointF (8, 19),
				new PointF (12, 30),
				new PointF (17, 28),
				new PointF (13, 18),
				new PointF (22, 18),
			});
		}

		static void DrawIBeamCursor (Graphics graphics)
		{
			DrawOutlinedLine (graphics, 16, 6, 16, 26);
			DrawOutlinedLine (graphics, 11, 6, 21, 6);
			DrawOutlinedLine (graphics, 11, 26, 21, 26);
		}

		static void DrawCrossCursor (Graphics graphics)
		{
			DrawOutlinedLine (graphics, 16, 5, 16, 27);
			DrawOutlinedLine (graphics, 5, 16, 27, 16);
		}

		static void DrawHandCursor (Graphics graphics)
		{
			DrawOutlinedLine (graphics, 8, 16, 8, 7);
			DrawOutlinedLine (graphics, 12, 17, 12, 5);
			DrawOutlinedLine (graphics, 16, 18, 16, 8);
			DrawOutlinedLine (graphics, 20, 19, 20, 11);
			DrawOutlinedLine (graphics, 8, 18, 15, 27);
			DrawOutlinedLine (graphics, 15, 27, 23, 24);
			DrawOutlinedLine (graphics, 23, 24, 22, 16);
		}

		static void DrawNoCursor (Graphics graphics)
		{
			using (Pen white = new Pen (Color.White, 5))
			using (Pen red = new Pen (Color.FromArgb (220, 30, 30), 3))
			using (Pen black = new Pen (Color.Black, 1)) {
				graphics.DrawEllipse (white, 7, 7, 18, 18);
				graphics.DrawEllipse (red, 8, 8, 16, 16);
				graphics.DrawEllipse (black, 8, 8, 16, 16);
				graphics.DrawLine (white, 10, 23, 23, 10);
				graphics.DrawLine (red, 11, 22, 22, 11);
				graphics.DrawLine (black, 11, 22, 22, 11);
			}
		}

		static void DrawSizeAllCursor (Graphics graphics)
		{
			DrawOutlinedLine (graphics, 16, 5, 16, 27);
			DrawOutlinedLine (graphics, 5, 16, 27, 16);
			FillOutlinedPolygon (graphics, new [] { new PointF (16, 3), new PointF (12, 8), new PointF (20, 8) });
			FillOutlinedPolygon (graphics, new [] { new PointF (16, 29), new PointF (12, 24), new PointF (20, 24) });
			FillOutlinedPolygon (graphics, new [] { new PointF (3, 16), new PointF (8, 12), new PointF (8, 20) });
			FillOutlinedPolygon (graphics, new [] { new PointF (29, 16), new PointF (24, 12), new PointF (24, 20) });
		}

		static void DrawHorizontalResizeCursor (Graphics graphics)
		{
			DrawOutlinedLine (graphics, 6, 16, 26, 16);
			FillOutlinedPolygon (graphics, new [] { new PointF (4, 16), new PointF (10, 11), new PointF (10, 21) });
			FillOutlinedPolygon (graphics, new [] { new PointF (28, 16), new PointF (22, 11), new PointF (22, 21) });
		}

		static void DrawVerticalResizeCursor (Graphics graphics)
		{
			DrawOutlinedLine (graphics, 16, 6, 16, 26);
			FillOutlinedPolygon (graphics, new [] { new PointF (16, 4), new PointF (11, 10), new PointF (21, 10) });
			FillOutlinedPolygon (graphics, new [] { new PointF (16, 28), new PointF (11, 22), new PointF (21, 22) });
		}

		static void DrawDiagonalResizeCursor (Graphics graphics, bool nesw)
		{
			if (nesw) {
				DrawOutlinedLine (graphics, 9, 23, 23, 9);
				FillOutlinedPolygon (graphics, new [] { new PointF (25, 7), new PointF (18, 8), new PointF (24, 14) });
				FillOutlinedPolygon (graphics, new [] { new PointF (7, 25), new PointF (8, 18), new PointF (14, 24) });
			} else {
				DrawOutlinedLine (graphics, 9, 9, 23, 23);
				FillOutlinedPolygon (graphics, new [] { new PointF (7, 7), new PointF (14, 8), new PointF (8, 14) });
				FillOutlinedPolygon (graphics, new [] { new PointF (25, 25), new PointF (18, 24), new PointF (24, 18) });
			}
		}

		static void DrawWaitCursor (Graphics graphics)
		{
			DrawOutlinedLine (graphics, 10, 6, 22, 6);
			DrawOutlinedLine (graphics, 10, 26, 22, 26);
			DrawOutlinedLine (graphics, 11, 7, 21, 25);
			DrawOutlinedLine (graphics, 21, 7, 11, 25);
		}

		static void DrawSmallWaitCursor (Graphics graphics)
		{
			DrawOutlinedLine (graphics, 22, 21, 29, 21);
			DrawOutlinedLine (graphics, 22, 30, 29, 30);
			DrawOutlinedLine (graphics, 23, 22, 28, 29);
			DrawOutlinedLine (graphics, 28, 22, 23, 29);
		}

		static void DrawQuestionMark (Graphics graphics)
		{
			using (Font font = new Font (FontFamily.GenericSansSerif, 14, FontStyle.Bold, GraphicsUnit.Pixel))
			using (Brush white = new SolidBrush (Color.White))
			using (Brush black = new SolidBrush (Color.Black)) {
				graphics.DrawString ("?", font, white, 19, 16);
				graphics.DrawString ("?", font, black, 18, 15);
			}
		}

		static void DrawUpArrowCursor (Graphics graphics)
		{
			FillOutlinedPolygon (graphics, new [] {
				new PointF (16, 2),
				new PointF (6, 14),
				new PointF (12, 14),
				new PointF (12, 28),
				new PointF (20, 28),
				new PointF (20, 14),
				new PointF (26, 14),
			});
		}

		static void DrawOutlinedLine (Graphics graphics, float x1, float y1, float x2, float y2)
		{
			using (Pen white = new Pen (Color.White, 4))
			using (Pen black = new Pen (Color.Black, 2)) {
				white.StartCap = white.EndCap = LineCap.Round;
				black.StartCap = black.EndCap = LineCap.Round;
				graphics.DrawLine (white, x1, y1, x2, y2);
				graphics.DrawLine (black, x1, y1, x2, y2);
			}
		}

		static void FillOutlinedPolygon (Graphics graphics, PointF [] points)
		{
			using (Brush white = new SolidBrush (Color.White))
			using (Pen black = new Pen (Color.Black, 1.5f)) {
				black.LineJoin = LineJoin.Round;
				graphics.FillPolygon (white, points);
				graphics.DrawPolygon (black, points);
			}
		}

		void DestroyNativeWindow (WaylandWindow window)
		{
			WaylandConnection liveConnection = connection;

			// Destroying or recreating a parent wl_surface invalidates all server
			// side wl_subsurface relationships below it.  Keep that invariant here
			// so callers such as SetOwner/SetParent cannot leave child HWNDs with
			// stale subsurface ids that later make place_above/set_position invalid.
			DestroyChildNativeWindows (window);

			foreach (WaylandShmBuffer buffer in window.Buffers) {
				waylandBuffers.Remove (buffer.BufferId);
				// This is teardown, not a live rendering path.  If the display
				// connection already failed, there is no valid server object left
				// to destroy; still release the local bitmap/fd state below.
				if (liveConnection != null)
					buffer.DestroyWaylandObject (liveConnection);
				else
					buffer.Dispose ();
			}
			window.Buffers.Clear ();

			if (liveConnection != null) {
				if (window.XdgPopupId != 0)
					liveConnection.SendRequest (window.XdgPopupId, WaylandProtocol.XdgPopup.Destroy, null);
				if (window.XdgToplevelId != 0)
					liveConnection.SendRequest (window.XdgToplevelId, WaylandProtocol.XdgToplevel.Destroy, null);
				if (window.XdgSurfaceId != 0)
					liveConnection.SendRequest (window.XdgSurfaceId, WaylandProtocol.XdgSurface.Destroy, null);
				if (window.SubsurfaceId != 0)
					liveConnection.SendRequest (window.SubsurfaceId, WaylandProtocol.WlSubsurface.Destroy, null);
				if (window.SurfaceId != 0)
					liveConnection.SendRequest (window.SurfaceId, WaylandProtocol.WlSurface.Destroy, null);
			}

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
			window.BufferAttached = false;
		}

		void UnmapNativeWindow (WaylandWindow window)
		{
			DetachSurfaceBuffer (window);
		}

		void DetachSurfaceBuffer (WaylandWindow window)
		{
			if (window.SurfaceId == 0 || !window.BufferAttached)
				return;

			WaylandConnection liveConnection = RequireConnection ();
			liveConnection.SendRequest (window.SurfaceId, WaylandProtocol.WlSurface.Attach, delegate (WaylandRequestBuilder b) {
				b.WriteObject (0);
				b.WriteInt32 (0);
				b.WriteInt32 (0);
			});
			liveConnection.SendRequest (window.SurfaceId, WaylandProtocol.WlSurface.Commit, null);
			window.BufferAttached = false;
		}

		void DestroyChildNativeWindows (WaylandWindow parent)
		{
			List<WaylandWindow> children = new List<WaylandWindow> ();
			foreach (WaylandWindow candidate in windows.Values) {
				if (candidate.Hwnd.Parent == parent.Hwnd)
					children.Add (candidate);
			}

			foreach (WaylandWindow child in children)
				DestroyNativeWindow (child);
		}

		void InvalidateWindowTree (WaylandWindow window)
		{
			Invalidate (window.Hwnd.Handle, Rectangle.Empty, false);

			List<WaylandWindow> children = new List<WaylandWindow> ();
			foreach (WaylandWindow candidate in windows.Values) {
				if (candidate.Hwnd.Parent == window.Hwnd)
					children.Add (candidate);
			}

			foreach (WaylandWindow child in children)
				InvalidateWindowTree (child);
		}

		void DispatchWaylandPending (int timeoutMilliseconds)
		{
			WaylandConnection liveConnection = RequireConnection ();

			try {
				WaylandMessage message;
				while (liveConnection.TryReadMessage (timeoutMilliseconds, out message)) {
					HandleWaylandMessage (message);
					timeoutMilliseconds = 0;
				}
				} catch (Exception e) {
					Console.Error.WriteLine ("Wayland dispatch failed: {0}", e.Message);
					if (connection == liveConnection)
						connection = null;
					liveConnection.Dispose ();
					throw;
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
				WaylandConnection liveConnection = RequireConnection ();
				liveConnection.SendRequest (xdgWmBaseId, WaylandProtocol.XdgWmBase.Pong, delegate (WaylandRequestBuilder b) {
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
						releasedBuffer.DestroyWaylandObject (RequireConnection ());
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
					WaylandConnection liveConnection = RequireConnection ();
					liveConnection.SendRequest (window.XdgSurfaceId, WaylandProtocol.XdgSurface.AckConfigure, delegate (WaylandRequestBuilder b) {
						b.WriteUInt32 (serial);
					});
					window.XdgConfigured = true;
				// Popup forms such as PropertyGridDropDown host their real
				// contents in child HWNDs.  When the compositor maps the popup
					// role, repaint the whole HWND tree so child subsurfaces are
					// committed after the parent popup is configured.
					InvalidateWindowTree (window);
					liveConnection.SendRequest (window.SurfaceId, WaylandProtocol.WlSurface.Commit, null);
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

			WaylandConnection liveConnection = RequireConnection ();
			WaylandMessageReader reader = new WaylandMessageReader (message.Payload);
			uint capabilities = reader.ReadUInt32 ();

			if ((capabilities & WaylandProtocol.WlSeat.CapabilityPointer) != 0) {
				if (pointerId == 0) {
					pointerId = liveConnection.AllocateId ();
					liveConnection.SendRequest (seatId, WaylandProtocol.WlSeat.GetPointer, delegate (WaylandRequestBuilder b) {
						b.WriteNewId (pointerId);
					});
					EnsureCursorShapeDevice ();
				}
			} else if (pointerId != 0) {
				if (cursorShapeDeviceId != 0) {
					liveConnection.SendRequest (cursorShapeDeviceId, WaylandProtocol.WpCursorShapeDeviceV1.Destroy, null);
					cursorShapeDeviceId = 0;
				}
				liveConnection.SendRequest (pointerId, WaylandProtocol.WlPointer.Release, null);
				pointerId = 0;
				pointerWindow = null;
				mouseButtons = MouseButtons.None;
				pointerEnterSerial = 0;
			}

			if ((capabilities & WaylandProtocol.WlSeat.CapabilityKeyboard) != 0) {
				if (keyboardId == 0) {
					keyboardId = liveConnection.AllocateId ();
					liveConnection.SendRequest (seatId, WaylandProtocol.WlSeat.GetKeyboard, delegate (WaylandRequestBuilder b) {
						b.WriteNewId (keyboardId);
					});
				}
			} else if (keyboardId != 0) {
				liveConnection.SendRequest (keyboardId, WaylandProtocol.WlKeyboard.Release, null);
				keyboardId = 0;
				keyboardWindow = null;
				evdevKeysDown.Clear ();
				ResetKeyboardLayout ();
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

				pointerEnterSerial = lastInputSerial;
				pointerWindow = window;
				UpdatePointerPosition (window, x, y);
				ApplyCursor (window);
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

				if (grabWindow != IntPtr.Zero) {
					// WinForms mouse capture keeps routing pointer messages to
					// the grabbed HWND until Capture is released.  A compositor
					// leave while that grab is active must not clear our current
					// pointer surface; later button releases still need a live
					// surface context before ResolvePointerTarget redirects them
					// to grabWindow.
					return;
				}

				PostPointerMessage (Msg.WM_MOUSELEAVE, window, pointerSurfacePosition.X, pointerSurfacePosition.Y, 0, CurrentMessageTime ());
				if (pointerWindow == window) {
					pointerWindow = null;
					pointerEnterSerial = 0;
				}
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
				SetFocusWindow (GetPointerFocusWindow (target));
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
				HandleKeyboardKeymap (reader, message);
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
				keyboardModsDepressed = reader.ReadUInt32 ();
				keyboardModsLatched = reader.ReadUInt32 ();
				keyboardModsLocked = reader.ReadUInt32 ();
				keyboardGroup = reader.ReadUInt32 ();
				keyboardLayout.SetModifiers (keyboardModsDepressed, keyboardModsLatched, keyboardModsLocked, keyboardGroup);
				modifierKeys = keyboardLayout.ModifierKeys;
				return;
			}
		}

		void HandleKeyboardKeymap (WaylandMessageReader reader, WaylandMessage message)
		{
			uint format = reader.ReadUInt32 ();
			uint size = reader.ReadUInt32 ();

			if (format == WaylandProtocol.WlKeyboard.KeymapFormatNoKeymap) {
				ResetKeyboardLayout ();
				return;
			}

			if (message.Fds.Length == 0) {
				ResetKeyboardLayout ();
				return;
			}

			WaylandKeymap keymap;
			try {
				keymap = WaylandKeymap.ReadFromFd (format, message.Fds [0], size);
			} catch {
				ResetKeyboardLayout ();
				return;
			} finally {
				for (int i = 1; i < message.Fds.Length; i++)
					Syscall.close (message.Fds [i]);
			}

			InstallKeyboardLayout (keymap);
		}

		void InstallKeyboardLayout (WaylandKeymap keymap)
		{
			string diagnostic;
			IWaylandKeyboardLayout layout = ManagedXkbKeyboardLayout.TryCreate (keymap, out diagnostic);
			if (layout == null)
				layout = LibXkbCommonKeyboardLayout.TryCreate (keymap, out diagnostic);
			if (layout == null)
				layout = new PhysicalUsKeyboardLayout ();

			keyboardLayout.Dispose ();
			keyboardLayout = layout;
			keyboardLayout.SetModifiers (keyboardModsDepressed, keyboardModsLatched, keyboardModsLocked, keyboardGroup);
			modifierKeys = keyboardLayout.ModifierKeys;
			keyText.Clear ();
		}

		void ResetKeyboardLayout ()
		{
			keyboardLayout.Dispose ();
			keyboardLayout = new PhysicalUsKeyboardLayout ();
			modifierKeys = Keys.None;
			keyboardModsDepressed = 0;
			keyboardModsLatched = 0;
			keyboardModsLocked = 0;
			keyboardGroup = 0;
			keyText.Clear ();
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

			WaylandKeyResult result = keyboardLayout.TranslateKey (evdevKey, pressed);
			modifierKeys = keyboardLayout.ModifierKeys;
			Keys key = result.KeyCode;
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
			if (pressed && !String.IsNullOrEmpty (result.Text))
				keyText [GetKeyTextKey (msg)] = result.Text;
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
				WaylandConnection liveConnection = RequireConnection ();
				liveConnection.SendRequest (window.SurfaceId, WaylandProtocol.WlSurface.SetBufferScale, delegate (WaylandRequestBuilder b) {
					b.WriteInt32 (window.BufferScale);
				});
				Invalidate (window.Hwnd.Handle, Rectangle.Empty, false);
			}
			if (pointerWindow == window)
				ApplyCursor (window);

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

		bool HasPopupStyle (WaylandWindow window)
		{
			return StyleSet ((int) window.Hwnd.initial_style, WindowStyles.WS_POPUP);
		}

		bool IsPopupWindow (WaylandWindow window)
		{
			return HasPopupStyle (window) && GetPopupParentWindow (window) != null;
		}

		bool ShouldDeferUnownedPopup (WaylandWindow window)
		{
			if (!HasPopupStyle (window) || IsPopupWindow (window))
				return false;

			Control control = Control.FromHandle (window.Hwnd.Handle);
			return control != null && !(control is Form);
		}

		WaylandWindow GetPopupParentWindow (WaylandWindow window)
		{
			WaylandWindow owner = GetPopupOwnerWindow (window);
			return owner != null ? GetRootWindow (owner) : null;
		}

		WaylandWindow GetPopupOwnerWindow (WaylandWindow window)
		{
			if (!HasPopupStyle (window))
				return null;

			if (window.Hwnd.owner != null) {
				WaylandWindow owner;
				if (!windows.TryGetValue (window.Hwnd.owner.Handle, out owner))
					return null;

				return owner;
			}

			Control control = Control.FromHandle (window.Hwnd.Handle);
			MonthCalendar calendar = control as MonthCalendar;
			if (calendar != null && calendar.owner != null && calendar.owner.IsHandleCreated) {
				// DateTimePicker creates its drop-down MonthCalendar as a
				// managed top-level WS_POPUP and records the owner on the
				// MonthCalendar instead of calling XplatUI.SetOwner.  Wayland
				// still needs the owner xdg_surface to make this an xdg_popup
				// rather than a separate application toplevel.
				WaylandWindow owner;
				if (windows.TryGetValue (calendar.owner.Handle, out owner))
					return owner;
			}

			return null;
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

		bool TryGetSubsurfaceGeometry (WaylandWindow window, out Point surfacePosition, out Rectangle sourceLogical)
		{
			sourceLogical = Rectangle.Empty;
			surfacePosition = new Point (window.Hwnd.X, window.Hwnd.Y);

			WaylandWindow immediateParent = GetParentWindow (window);
			if (immediateParent == null)
				return false;

			Point childScreen = GetWindowScreenLocation (window);
			Rectangle visibleScreen = new Rectangle (childScreen.X, childScreen.Y,
				Math.Max (1, window.Hwnd.ClientRect.Width), Math.Max (1, window.Hwnd.ClientRect.Height));

			Hwnd parentHwnd = window.Hwnd.Parent;
			while (parentHwnd != null) {
				WaylandWindow parent;
				if (!windows.TryGetValue (parentHwnd.Handle, out parent))
					return false;

				Point parentScreen = GetWindowScreenLocation (parent);
				Rectangle parentClient = parent.Hwnd.ClientRect;
				// The Wayland surface we commit is the Mono client buffer,
				// whose logical origin is always 0,0 even when Hwnd.ClientRect
				// has a non-client offset in Mono's window bookkeeping.
				Rectangle parentClip = new Rectangle (parentScreen.X, parentScreen.Y,
					Math.Max (0, parentClient.Width), Math.Max (0, parentClient.Height));
				visibleScreen = Rectangle.Intersect (visibleScreen, parentClip);
				if (visibleScreen.Width <= 0 || visibleScreen.Height <= 0)
					return false;

				parentHwnd = parent.Hwnd.Parent;
			}

			sourceLogical = new Rectangle (visibleScreen.X - childScreen.X, visibleScreen.Y - childScreen.Y,
				visibleScreen.Width, visibleScreen.Height);
			// If the parent is itself clipped, its committed surface origin is
			// already shifted to the parent's visible source rectangle.  Child
			// subsurface positions are relative to that committed surface.
			Point parentOrigin = GetSurfaceScreenLocation (immediateParent);
			surfacePosition = new Point (visibleScreen.X - parentOrigin.X, visibleScreen.Y - parentOrigin.Y);
			return true;
		}

		Point GetSurfaceScreenLocation (WaylandWindow window)
		{
			if (window.SubsurfaceId != 0) {
				Point surfacePosition;
				Rectangle sourceLogical;
				if (TryGetSubsurfaceGeometry (window, out surfacePosition, out sourceLogical)) {
					Point windowOrigin = GetWindowScreenLocation (window);
					return new Point (windowOrigin.X + sourceLogical.X, windowOrigin.Y + sourceLogical.Y);
				}
			}

			return GetWindowScreenLocation (window);
		}

		void TranslateSurfacePointToWindow (WaylandWindow window, ref int x, ref int y)
		{
			if (window.SubsurfaceId == 0)
				return;

			Point surfacePosition;
			Rectangle sourceLogical;
			if (TryGetSubsurfaceGeometry (window, out surfacePosition, out sourceLogical)) {
				x += sourceLogical.X;
				y += sourceLogical.Y;
			}
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
			WaylandWindow active = GetActivationWindow (window);
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

		WaylandWindow GetActivationWindow (WaylandWindow window)
		{
			WaylandWindow root = GetRootWindow (window);
			if (root == null)
				return null;

			if (IsPopupWindow (root)) {
				// WinForms owned popups are transient UI for their owner, not
				// independent active forms.  Child controls inside a popup must
				// keep the owner active; otherwise PropertyGridView sees its
				// owner receive WM_ACTIVATE/WA_INACTIVE and closes the dropdown
				// while processing an inside click such as a tab or scrollbar.
				WaylandWindow parent = GetPopupParentWindow (root);
				if (parent != null)
					return parent;
			}

			return root;
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

		IntPtr GetPointerFocusWindow (WaylandWindow target)
		{
			WaylandWindow root = GetRootWindow (target);
			if (root != null && IsPopupWindow (root)) {
				Control control = Control.FromHandle (target.Hwnd.Handle);
				if (control != null && !control.ActivateOnShow) {
					// ComboBox transfers mouse capture to its ComboListBox
					// popup after opening.  That grab, not focus, owns the mouse
					// stream: WM_LBUTTONUP on the popup commits the clicked item,
					// while WM_KILLFOCUS on the owner dismisses the list first.
					// Keep focus on the logical owner while the grabbed popup
					// receives the pointer messages.
					WaylandWindow owner = GetPopupOwnerWindow (root);
					return owner != null ? owner.Hwnd.Handle : focusWindow;
				}
			}

			return target.Hwnd.Handle;
		}

		void UpdatePointerPosition (WaylandWindow window, int x, int y)
		{
			pointerSurfacePosition = new Point (x, y);
			Point origin = GetSurfaceScreenLocation (window);
			mousePosition = new Point (origin.X + x, origin.Y + y);
		}

		WaylandWindow ResolvePointerTarget (WaylandWindow surfaceWindow, int surfaceX, int surfaceY, out int targetX, out int targetY)
		{
			WaylandWindow target = surfaceWindow;
			targetX = surfaceX;
			targetY = surfaceY;
			TranslateSurfacePointToWindow (surfaceWindow, ref targetX, ref targetY);

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

			MonthCalendar calendar = control as MonthCalendar;
			if (calendar != null && calendar.owner != null) {
				// The DateTimePicker owns the drop-down state.  Hiding only the
				// MonthCalendar would leave the picker thinking it is still open.
				calendar.owner.HideMonthCalendar ();
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

		static Keys MapKeysymToKeys (uint keysym)
		{
			if (keysym >= 'a' && keysym <= 'z')
				return (Keys) ((int) Keys.A + (int) (keysym - 'a'));
			if (keysym >= 'A' && keysym <= 'Z')
				return (Keys) ((int) Keys.A + (int) (keysym - 'A'));
			if (keysym >= '0' && keysym <= '9')
				return (Keys) ((int) Keys.D0 + (int) (keysym - '0'));
			if (keysym >= 0xffbe && keysym <= 0xffc9)
				return (Keys) ((int) Keys.F1 + (int) (keysym - 0xffbe));
			if (keysym >= 0xffb0 && keysym <= 0xffb9)
				return (Keys) ((int) Keys.NumPad0 + (int) (keysym - 0xffb0));

			switch (keysym) {
			case 0xff1b: return Keys.Escape;
			case 0xff08: return Keys.Back;
			case 0xff09: return Keys.Tab;
			case 0xff0d: return Keys.Return;
			case 0xffff: return Keys.Delete;
			case 0xff63: return Keys.Insert;
			case 0xff50: return Keys.Home;
			case 0xff57: return Keys.End;
			case 0xff55: return Keys.PageUp;
			case 0xff56: return Keys.PageDown;
			case 0xff51: return Keys.Left;
			case 0xff52: return Keys.Up;
			case 0xff53: return Keys.Right;
			case 0xff54: return Keys.Down;
			case 0xffe1:
			case 0xffe2: return Keys.ShiftKey;
			case 0xffe3:
			case 0xffe4: return Keys.ControlKey;
			case 0xffe9:
			case 0xffea: return Keys.Menu;
			case 0xffeb: return Keys.LWin;
			case 0xffec: return Keys.RWin;
			case 0xffe5: return Keys.CapsLock;
			case 0xff7f: return Keys.NumLock;
			case 0xff14: return Keys.Scroll;
			case 0xff8d: return Keys.Return;
			case 0xffaa: return Keys.Multiply;
			case 0xffab: return Keys.Add;
			case 0xffad: return Keys.Subtract;
			case 0xffae: return Keys.Decimal;
			case 0xffaf: return Keys.Divide;
			case ' ': return Keys.Space;
			case '-':
			case '_': return Keys.OemMinus;
			case '=':
			case '+': return Keys.Oemplus;
			case '[':
			case '{': return Keys.OemOpenBrackets;
			case ']':
			case '}': return Keys.OemCloseBrackets;
			case '\\':
			case '|': return Keys.OemPipe;
			case ';':
			case ':': return Keys.OemSemicolon;
			case '\'':
			case '"': return Keys.OemQuotes;
			case '`':
			case '~': return Keys.Oemtilde;
			case ',':
			case '<': return Keys.Oemcomma;
			case '.':
			case '>': return Keys.OemPeriod;
			case '/':
			case '?': return Keys.OemQuestion;
			default: return Keys.None;
			}
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

			Point surfacePosition;
			Rectangle sourceLogical;
			if (!TryGetSubsurfaceGeometry (window, out surfacePosition, out sourceLogical)) {
				DetachSurfaceBuffer (window);
				return;
			}

			SendSubsurfacePosition (window, surfacePosition);
		}

		void SendSubsurfacePosition (WaylandWindow window, Point surfacePosition)
		{
			WaylandConnection liveConnection = RequireConnection ();
			liveConnection.SendRequest (window.SubsurfaceId, WaylandProtocol.WlSubsurface.SetPosition, delegate (WaylandRequestBuilder b) {
				b.WriteInt32 (surfacePosition.X);
				b.WriteInt32 (surfacePosition.Y);
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

			WaylandConnection liveConnection = RequireConnection ();
			liveConnection.SendRequest (window.SubsurfaceId, WaylandProtocol.WlSubsurface.PlaceAbove, delegate (WaylandRequestBuilder b) {
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
