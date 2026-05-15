using System;
using System.IO;
using System.Net.Sockets;
using Mono.Unix;

namespace WaylandDriver.Wayland {
	internal sealed class WaylandConnection : IDisposable {
		readonly Socket socket;
		readonly object writeLock = new object ();
		uint nextObjectId = 2;

		WaylandConnection (Socket socket)
		{
			this.socket = socket;
		}

		public static WaylandConnection ConnectFromEnvironment ()
		{
			string path = ResolveDisplayPath ();
			Socket socket = new Socket (AddressFamily.Unix, SocketType.Stream, ProtocolType.IP);
			socket.Connect (new UnixEndPoint (path));
			return new WaylandConnection (socket);
		}

		public uint AllocateId ()
		{
			return nextObjectId++;
		}

		public void SendRequest (uint objectId, ushort opcode, Action<WaylandRequestBuilder> build)
		{
			WaylandRequestBuilder builder = new WaylandRequestBuilder ();
			if (build != null)
				build (builder);

			byte [] bytes = builder.ToArray (objectId, opcode);
			lock (writeLock) {
				int offset = 0;
				while (offset < bytes.Length)
					offset += socket.Send (bytes, offset, bytes.Length - offset, SocketFlags.None);
			}
		}

		public bool TryReadMessage (int timeoutMilliseconds, out WaylandMessage message)
		{
			message = null;

			if (timeoutMilliseconds >= 0 && !socket.Poll (timeoutMilliseconds * 1000, SelectMode.SelectRead))
				return false;

			byte [] header = ReadExactly (8);
			uint objectId = ReadUInt32 (header, 0);
			uint second = ReadUInt32 (header, 4);
			ushort opcode = (ushort) (second & 0xffff);
			ushort size = (ushort) (second >> 16);

			if (size < 8)
				throw new InvalidDataException ("Invalid Wayland message size.");

			message = new WaylandMessage (objectId, opcode, ReadExactly (size - 8));
			return true;
		}

		public WaylandRegistry GetRegistryRoundtrip ()
		{
			uint registryId = AllocateId ();
			uint callbackId = AllocateId ();
			WaylandRegistry registry = new WaylandRegistry (registryId);

			SendRequest (1, 1, delegate (WaylandRequestBuilder b) {
				b.WriteNewId (registryId);
			});
			SendRequest (1, 0, delegate (WaylandRequestBuilder b) {
				b.WriteNewId (callbackId);
			});

			bool done = false;
			while (!done) {
				WaylandMessage message;
				TryReadMessage (-1, out message);

				if (message.ObjectId == registryId) {
					registry.HandleEvent (message);
				} else if (message.ObjectId == callbackId && message.Opcode == 0) {
					done = true;
				}
			}

			return registry;
		}

		public uint Bind (WaylandRegistry registry, string iface, uint maxVersion)
		{
			WaylandGlobal global = registry.Find (iface);
			if (global == null)
				return 0;

			uint version = Math.Min (global.Version, maxVersion);
			uint objectId = AllocateId ();
			SendRequest (registry.ObjectId, 0, delegate (WaylandRequestBuilder b) {
				b.WriteUInt32 (global.Name);
				b.WriteString (global.Interface);
				b.WriteUInt32 (version);
				b.WriteNewId (objectId);
			});
			return objectId;
		}

		public void Dispose ()
		{
			socket.Close ();
		}

		static string ResolveDisplayPath ()
		{
			string display = Environment.GetEnvironmentVariable ("WAYLAND_DISPLAY");
			if (String.IsNullOrEmpty (display))
				display = "wayland-0";

			if (display.IndexOf ('/') >= 0)
				return display;

			string runtimeDir = Environment.GetEnvironmentVariable ("XDG_RUNTIME_DIR");
			if (String.IsNullOrEmpty (runtimeDir))
				throw new InvalidOperationException ("XDG_RUNTIME_DIR is not set; cannot locate Wayland display " + display + ".");

			return Path.Combine (runtimeDir, display);
		}

		byte [] ReadExactly (int count)
		{
			byte [] buffer = new byte [count];
			int offset = 0;
			while (offset < count) {
				int n = socket.Receive (buffer, offset, count - offset, SocketFlags.None);
				if (n == 0)
					throw new EndOfStreamException ("Wayland compositor closed the connection.");
				offset += n;
			}
			return buffer;
		}

		static uint ReadUInt32 (byte [] buffer, int offset)
		{
			return (uint) buffer [offset] |
				((uint) buffer [offset + 1] << 8) |
				((uint) buffer [offset + 2] << 16) |
				((uint) buffer [offset + 3] << 24);
		}
	}
}

