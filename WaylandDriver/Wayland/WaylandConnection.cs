using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using Mono.Unix;
using Mono.Unix.Native;

namespace WaylandDriver.Wayland {
	internal sealed class WaylandConnection : IDisposable {
		readonly Socket socket;
		readonly object writeLock = new object ();
		uint nextObjectId = 2;
		bool disposed;

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

		public unsafe void SendRequestWithFd (uint objectId, ushort opcode, Action<WaylandRequestBuilder> build, int fd)
		{
			WaylandRequestBuilder builder = new WaylandRequestBuilder ();
			if (build != null)
				build (builder);

			byte [] bytes = builder.ToArray (objectId, opcode);
			byte [] control = new byte [checked ((int) Syscall.CMSG_SPACE (sizeof (int)))];
			Msghdr message = new Msghdr {
				msg_control = control,
				msg_controllen = control.Length,
			};
			Cmsghdr header = new Cmsghdr {
				cmsg_len = (long) Syscall.CMSG_LEN (sizeof (int)),
				cmsg_level = UnixSocketProtocol.SOL_SOCKET,
				cmsg_type = UnixSocketControlMessage.SCM_RIGHTS,
			};

			header.WriteToBuffer (message, 0);
			long dataOffset = Syscall.CMSG_DATA (message, 0);
			fixed (byte* controlPtr = message.msg_control) {
				((int*) (controlPtr + dataOffset)) [0] = fd;
			}

			fixed (byte* bytesPtr = bytes) {
				message.msg_iov = new [] {
					new Iovec {
						iov_base = (IntPtr) bytesPtr,
						iov_len = (ulong) bytes.Length,
					}
				};
				message.msg_iovlen = 1;

				lock (writeLock) {
					long written = Syscall.sendmsg (socket.Handle.ToInt32 (), message, 0);
					if (written < 0)
						UnixMarshal.ThrowExceptionForLastError ();
					if (written != bytes.Length)
						throw new IOException ("Wayland fd request was only partially written.");
				}
			}
		}

		public bool TryReadMessage (int timeoutMilliseconds, out WaylandMessage message)
		{
			message = null;

			if (timeoutMilliseconds >= 0 && !socket.Poll (timeoutMilliseconds * 1000, SelectMode.SelectRead))
				return false;

			List<int> fds = new List<int> ();
			byte [] header = ReadExactly (8, fds);
			uint objectId = ReadUInt32 (header, 0);
			uint second = ReadUInt32 (header, 4);
			ushort opcode = (ushort) (second & 0xffff);
			ushort size = (ushort) (second >> 16);

			if (size < 8)
				throw new InvalidDataException ("Invalid Wayland message size.");

			message = new WaylandMessage (objectId, opcode, ReadExactly (size - 8, fds), fds.ToArray ());
			return true;
		}

		public WaylandRegistry GetRegistryRoundtrip ()
		{
			uint registryId = AllocateId ();
			uint callbackId = AllocateId ();
			WaylandRegistry registry = new WaylandRegistry (registryId);

			SendRequest (1, WaylandProtocol.WlDisplay.GetRegistry, delegate (WaylandRequestBuilder b) {
				b.WriteNewId (registryId);
			});
			SendRequest (1, WaylandProtocol.WlDisplay.Sync, delegate (WaylandRequestBuilder b) {
				b.WriteNewId (callbackId);
			});

			bool done = false;
			while (!done) {
				WaylandMessage message;
				TryReadMessage (-1, out message);

				if (message.ObjectId == registryId) {
					registry.HandleEvent (message);
				} else if (message.ObjectId == callbackId && message.Opcode == WaylandProtocol.WlCallback.Done) {
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

			return Bind (registry, global, maxVersion);
		}

		public uint Bind (WaylandRegistry registry, WaylandGlobal global, uint maxVersion)
		{
			uint version = Math.Min (global.Version, maxVersion);
			uint objectId = AllocateId ();
			SendRequest (registry.ObjectId, WaylandProtocol.WlRegistry.Bind, delegate (WaylandRequestBuilder b) {
				b.WriteUInt32 (global.Name);
				b.WriteString (global.Interface);
				b.WriteUInt32 (version);
				b.WriteNewId (objectId);
			});
			return objectId;
		}

		public void Dispose ()
		{
			lock (writeLock) {
				if (disposed)
					return;

				disposed = true;
				socket.Close ();
			}
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

		byte [] ReadExactly (int count, List<int> fds)
		{
			byte [] buffer = new byte [count];
			int offset = 0;
			while (offset < count) {
				int n = ReceiveSome (buffer, offset, count - offset, fds);
				if (n == 0)
					throw new EndOfStreamException ("Wayland compositor closed the connection.");
				offset += n;
			}
			return buffer;
		}

		unsafe int ReceiveSome (byte [] buffer, int offset, int count, List<int> fds)
		{
			byte [] control = new byte [checked ((int) Syscall.CMSG_SPACE (sizeof (int) * 8))];
			Msghdr message = new Msghdr {
				msg_control = control,
				msg_controllen = control.Length,
			};

			fixed (byte* bytesPtr = buffer) {
				message.msg_iov = new [] {
					new Iovec {
						iov_base = (IntPtr) (bytesPtr + offset),
						iov_len = (ulong) count,
					}
				};
				message.msg_iovlen = 1;

				long received = Syscall.recvmsg (socket.Handle.ToInt32 (), message, 0);
				if (received < 0)
					UnixMarshal.ThrowExceptionForLastError ();

				ReadReceivedFds (message, fds);
				return (int) received;
			}
		}

		unsafe static void ReadReceivedFds (Msghdr message, List<int> fds)
		{
			long offset = Syscall.CMSG_FIRSTHDR (message);
			while (offset != -1) {
				Cmsghdr header = Cmsghdr.ReadFromBuffer (message, offset);
				if (header.cmsg_level == UnixSocketProtocol.SOL_SOCKET &&
				    header.cmsg_type == UnixSocketControlMessage.SCM_RIGHTS) {
					long dataOffset = Syscall.CMSG_DATA (message, offset);
					long headerLength = (long) Syscall.CMSG_LEN (0);
					int byteCount = checked ((int) (header.cmsg_len - headerLength));

					fixed (byte* controlPtr = message.msg_control) {
						int* fdPtr = (int*) (controlPtr + dataOffset);
						for (int i = 0; i < byteCount / sizeof (int); i++)
							fds.Add (fdPtr [i]);
					}
				}

				offset = Syscall.CMSG_NXTHDR (message, offset);
			}
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
