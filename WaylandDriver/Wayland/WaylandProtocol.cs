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

		internal static class XdgWmBase {
			public const ushort Destroy = 0;
			public const ushort GetXdgSurface = 2;
			public const ushort Pong = 3;

			public const ushort Ping = 0;
		}

		internal static class XdgSurface {
			public const ushort Destroy = 0;
			public const ushort GetToplevel = 1;
			public const ushort AckConfigure = 4;

			public const ushort Configure = 0;
		}

		internal static class XdgToplevel {
			public const ushort Destroy = 0;
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
	}
}
