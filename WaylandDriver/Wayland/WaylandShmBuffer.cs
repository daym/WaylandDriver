using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Mono.Unix;
using Mono.Unix.Native;

namespace WaylandDriver.Wayland {
	internal sealed class WaylandShmBuffer : IDisposable {
		public readonly uint BufferId;
		public readonly int LogicalWidth;
		public readonly int LogicalHeight;
		public readonly int BufferWidth;
		public readonly int BufferHeight;
		public readonly int Scale;
		public readonly int Stride;
		public readonly int Size;

		IntPtr data;
		bool disposed;

		WaylandShmBuffer (uint bufferId, int logicalWidth, int logicalHeight, int bufferWidth, int bufferHeight, int scale, IntPtr data, int stride, int size)
		{
			BufferId = bufferId;
			LogicalWidth = logicalWidth;
			LogicalHeight = logicalHeight;
			Scale = scale;
			BufferWidth = bufferWidth;
			BufferHeight = bufferHeight;
			Stride = stride;
			Size = size;
			this.data = data;
		}

		public static WaylandShmBuffer CreateFromBitmap (WaylandConnection connection, uint shmId, Bitmap bitmap, int scale)
		{
			if (scale < 1)
				scale = 1;

			int bufferWidth = Math.Max (1, bitmap.Width);
			int bufferHeight = Math.Max (1, bitmap.Height);
			int logicalWidth = Math.Max (1, (bufferWidth + scale - 1) / scale);
			int logicalHeight = Math.Max (1, (bufferHeight + scale - 1) / scale);
			int stride = checked (bufferWidth * 4);
			int size = checked (stride * bufferHeight);
			int fd = CreateAnonymousShmFile (size);
			IntPtr mapped = IntPtr.Zero;

			try {
				mapped = Syscall.mmap (IntPtr.Zero, (ulong) size, MmapProts.PROT_READ | MmapProts.PROT_WRITE, MmapFlags.MAP_SHARED, fd, 0);
				if (mapped == new IntPtr (-1)) {
					mapped = IntPtr.Zero;
					UnixMarshal.ThrowExceptionForLastError ();
				}

				uint poolId = connection.AllocateId ();
				uint bufferId = connection.AllocateId ();

				connection.SendRequestWithFd (shmId, WaylandProtocol.WlShm.CreatePool, delegate (WaylandRequestBuilder b) {
					b.WriteNewId (poolId);
					b.WriteInt32 (size);
				}, fd);
				connection.SendRequest (poolId, WaylandProtocol.WlShmPool.CreateBuffer, delegate (WaylandRequestBuilder b) {
					b.WriteNewId (bufferId);
					b.WriteInt32 (0);
					b.WriteInt32 (bufferWidth);
					b.WriteInt32 (bufferHeight);
					b.WriteInt32 (stride);
					b.WriteUInt32 (WaylandProtocol.WlShm.FormatArgb8888);
				});
				connection.SendRequest (poolId, WaylandProtocol.WlShmPool.Destroy, null);

				WaylandShmBuffer buffer = new WaylandShmBuffer (bufferId, logicalWidth, logicalHeight, bufferWidth, bufferHeight, scale, mapped, stride, size);
				buffer.CopyFromBitmap (bitmap);
				mapped = IntPtr.Zero;
				return buffer;
			} finally {
				if (mapped != IntPtr.Zero)
					Syscall.munmap (mapped, (ulong) size);
				Syscall.close (fd);
			}
		}

		public void DestroyWaylandObject (WaylandConnection connection)
		{
			if (connection != null)
				connection.SendRequest (BufferId, WaylandProtocol.WlBuffer.Destroy, null);
			Dispose ();
		}

		public void Dispose ()
		{
			if (disposed)
				return;
			disposed = true;

			if (data != IntPtr.Zero) {
				Syscall.munmap (data, (ulong) Size);
				data = IntPtr.Zero;
			}
		}

		unsafe void CopyFromBitmap (Bitmap bitmap)
		{
			Rectangle rect = new Rectangle (0, 0, bitmap.Width, bitmap.Height);
			BitmapData bits = bitmap.LockBits (rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
			try {
				byte* srcBase = (byte*) bits.Scan0;
				byte* dstBase = (byte*) data;

				for (int y = 0; y < BufferHeight; y++) {
					byte* src = srcBase + y * bits.Stride;
					byte* dst = dstBase + y * Stride;
					Buffer.MemoryCopy (src, dst, Stride, BufferWidth * 4);
				}
			} finally {
				bitmap.UnlockBits (bits);
			}
		}

		static int CreateAnonymousShmFile (int size)
		{
			string runtimeDir = Environment.GetEnvironmentVariable ("XDG_RUNTIME_DIR");
			if (String.IsNullOrEmpty (runtimeDir))
				runtimeDir = Path.GetTempPath ();

			string path = Path.Combine (runtimeDir, ".mono-wayland-shm-" +
				Process.GetCurrentProcess ().Id + "-" + Guid.NewGuid ().ToString ("N"));
			int fd = Syscall.open (path, OpenFlags.O_RDWR | OpenFlags.O_CREAT | OpenFlags.O_EXCL | OpenFlags.O_CLOEXEC,
				FilePermissions.S_IRUSR | FilePermissions.S_IWUSR);
			if (fd < 0)
				UnixMarshal.ThrowExceptionForLastError ();

			Syscall.unlink (path);

			if (Syscall.ftruncate (fd, size) != 0) {
				Errno error = Stdlib.GetLastError ();
				Syscall.close (fd);
				throw new IOException ("ftruncate failed while creating Wayland shared memory: " + error);
			}

			return fd;
		}
	}
}
