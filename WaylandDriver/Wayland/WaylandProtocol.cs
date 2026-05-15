namespace WaylandDriver.Wayland {
	internal static class WaylandProtocol {
		internal static class WlDisplay {
			public const ushort Sync = 0;
			public const ushort GetRegistry = 1;

			public const ushort Error = 0;
			public const ushort DeleteId = 1;
		}

		internal static class WlRegistry {
			public const ushort Bind = 0;

			public const ushort Global = 0;
			public const ushort GlobalRemove = 1;
		}

		internal static class WlCallback {
			public const ushort Done = 0;
		}

		internal static class WlCompositor {
			public const ushort CreateSurface = 0;
		}

		internal static class WlSubcompositor {
			public const ushort Destroy = 0;
			public const ushort GetSubsurface = 1;
		}

		internal static class WlSubsurface {
			public const ushort Destroy = 0;
			public const ushort SetPosition = 1;
			public const ushort PlaceAbove = 2;
			public const ushort PlaceBelow = 3;
			public const ushort SetSync = 4;
			public const ushort SetDesync = 5;
		}

		internal static class WlShm {
			public const ushort CreatePool = 0;

			public const uint FormatArgb8888 = 0;
		}

		internal static class WlShmPool {
			public const ushort CreateBuffer = 0;
			public const ushort Destroy = 1;
		}

		internal static class WlBuffer {
			public const ushort Destroy = 0;

			public const ushort Release = 0;
		}

		internal static class WlSurface {
			public const ushort Destroy = 0;
			public const ushort Attach = 1;
			public const ushort Damage = 2;
			public const ushort Commit = 6;
			public const ushort SetBufferScale = 8;
			public const ushort DamageBuffer = 9;

			public const ushort Enter = 0;
			public const ushort Leave = 1;
		}

		internal static class WlOutput {
			public const ushort Geometry = 0;
			public const ushort Mode = 1;
			public const ushort Done = 2;
			public const ushort Scale = 3;
		}

		internal static class WlSeat {
			public const ushort GetPointer = 0;
			public const ushort GetKeyboard = 1;

			public const ushort Capabilities = 0;
			public const ushort Name = 1;

			public const uint CapabilityPointer = 1;
			public const uint CapabilityKeyboard = 2;
		}

		internal static class WlPointer {
			public const ushort SetCursor = 0;
			public const ushort Release = 1;

			public const ushort Enter = 0;
			public const ushort Leave = 1;
			public const ushort Motion = 2;
			public const ushort Button = 3;
			public const ushort Axis = 4;
			public const ushort Frame = 5;
			public const ushort AxisSource = 6;
			public const ushort AxisStop = 7;
			public const ushort AxisDiscrete = 8;

			public const uint ButtonLeft = 0x110;
			public const uint ButtonRight = 0x111;
			public const uint ButtonMiddle = 0x112;
			public const uint ButtonStateReleased = 0;
			public const uint ButtonStatePressed = 1;
			public const uint AxisVerticalScroll = 0;
		}

		internal static class WlKeyboard {
			public const ushort Release = 0;

			public const ushort Keymap = 0;
			public const ushort Enter = 1;
			public const ushort Leave = 2;
			public const ushort Key = 3;
			public const ushort Modifiers = 4;
			public const ushort RepeatInfo = 5;

			public const uint KeymapFormatNoKeymap = 0;
			public const uint KeymapFormatXkbV1 = 1;
			public const uint KeyStateReleased = 0;
			public const uint KeyStatePressed = 1;
		}

		internal static class WlDataOffer {
			public const ushort Accept = 0;
			public const ushort Receive = 1;
			public const ushort Destroy = 2;

			public const ushort Offer = 0;
		}

		internal static class WlDataSource {
			public const ushort Offer = 0;
			public const ushort Destroy = 1;

			public const ushort Target = 0;
			public const ushort Send = 1;
			public const ushort Cancelled = 2;
		}

		internal static class WlDataDevice {
			public const ushort SetSelection = 1;

			public const ushort DataOffer = 0;
			public const ushort Selection = 5;
		}

		internal static class WlDataDeviceManager {
			public const ushort CreateDataSource = 0;
			public const ushort GetDataDevice = 1;
		}

		internal static class WpCursorShapeManagerV1 {
			public const ushort Destroy = 0;
			public const ushort GetPointer = 1;
		}

		internal static class WpCursorShapeDeviceV1 {
			public const ushort Destroy = 0;
			public const ushort SetShape = 1;

			public const uint Default = 1;
			public const uint ContextMenu = 2;
			public const uint Help = 3;
			public const uint Pointer = 4;
			public const uint Progress = 5;
			public const uint Wait = 6;
			public const uint Cell = 7;
			public const uint Crosshair = 8;
			public const uint Text = 9;
			public const uint VerticalText = 10;
			public const uint Alias = 11;
			public const uint Copy = 12;
			public const uint Move = 13;
			public const uint NoDrop = 14;
			public const uint NotAllowed = 15;
			public const uint Grab = 16;
			public const uint Grabbing = 17;
			public const uint EResize = 18;
			public const uint NResize = 19;
			public const uint NeResize = 20;
			public const uint NwResize = 21;
			public const uint SResize = 22;
			public const uint SeResize = 23;
			public const uint SwResize = 24;
			public const uint WResize = 25;
			public const uint EwResize = 26;
			public const uint NsResize = 27;
			public const uint NeswResize = 28;
			public const uint NwseResize = 29;
			public const uint ColResize = 30;
			public const uint RowResize = 31;
			public const uint AllScroll = 32;
			public const uint ZoomIn = 33;
			public const uint ZoomOut = 34;
		}

		internal static class XdgWmBase {
			public const ushort Destroy = 0;
			public const ushort CreatePositioner = 1;
			public const ushort GetXdgSurface = 2;
			public const ushort Pong = 3;

			public const ushort Ping = 0;
		}

		internal static class XdgPositioner {
			public const ushort Destroy = 0;
			public const ushort SetSize = 1;
			public const ushort SetAnchorRect = 2;
			public const ushort SetAnchor = 3;
			public const ushort SetGravity = 4;
			public const ushort SetConstraintAdjustment = 5;
			public const ushort SetOffset = 6;

			public const uint AnchorTopLeft = 5;
			public const uint GravityBottomRight = 8;
			public const uint ConstraintAdjustmentSlideX = 1;
			public const uint ConstraintAdjustmentSlideY = 2;
			public const uint ConstraintAdjustmentFlipX = 4;
			public const uint ConstraintAdjustmentFlipY = 8;
			public const uint ConstraintAdjustmentResizeX = 16;
			public const uint ConstraintAdjustmentResizeY = 32;
		}

		internal static class XdgSurface {
			public const ushort Destroy = 0;
			public const ushort GetToplevel = 1;
			public const ushort GetPopup = 2;
			public const ushort AckConfigure = 4;

			public const ushort Configure = 0;
		}

		internal static class XdgToplevel {
			public const ushort Destroy = 0;
			public const ushort SetParent = 1;
			public const ushort SetTitle = 2;
			public const ushort SetAppId = 3;
			public const ushort SetMaxSize = 7;
			public const ushort SetMinSize = 8;
			public const ushort SetMaximized = 9;
			public const ushort UnsetMaximized = 10;
			public const ushort SetMinimized = 13;

			public const ushort Configure = 0;
			public const ushort Close = 1;
		}

		internal static class XdgPopup {
			public const ushort Destroy = 0;
			public const ushort Grab = 1;

			public const ushort Configure = 0;
			public const ushort PopupDone = 1;
			public const ushort Repositioned = 2;
		}

		internal static class ZxdgDecorationManagerV1 {
			public const ushort Destroy = 0;
			public const ushort GetToplevelDecoration = 1;
		}

		internal static class ZxdgToplevelDecorationV1 {
			public const ushort Destroy = 0;
			public const ushort SetMode = 1;
			public const ushort UnsetMode = 2;

			public const ushort Configure = 0;

			public const uint ModeClientSide = 1;
			public const uint ModeServerSide = 2;
		}
	}
}
