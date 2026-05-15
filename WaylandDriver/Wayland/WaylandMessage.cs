using System;
using System.IO;
using System.Text;

namespace WaylandDriver.Wayland {
	internal sealed class WaylandMessage {
		public readonly uint ObjectId;
		public readonly ushort Opcode;
		public readonly byte [] Payload;
		public readonly int [] Fds;

		public WaylandMessage (uint objectId, ushort opcode, byte [] payload)
			: this (objectId, opcode, payload, null)
		{
		}

		public WaylandMessage (uint objectId, ushort opcode, byte [] payload, int [] fds)
		{
			ObjectId = objectId;
			Opcode = opcode;
			Payload = payload ?? new byte [0];
			Fds = fds ?? new int [0];
		}
	}

	internal sealed class WaylandMessageReader {
		readonly byte [] data;
		int offset;

		public WaylandMessageReader (byte [] data)
		{
			this.data = data ?? new byte [0];
		}

		public bool End {
			get { return offset >= data.Length; }
		}

		public int ReadInt32 ()
		{
			return unchecked ((int) ReadUInt32 ());
		}

		public uint ReadUInt32 ()
		{
			Require (4);
			uint value = (uint) data [offset] |
				((uint) data [offset + 1] << 8) |
				((uint) data [offset + 2] << 16) |
				((uint) data [offset + 3] << 24);
			offset += 4;
			return value;
		}

		public string ReadString ()
		{
			uint length = ReadUInt32 ();
			if (length == 0)
				return String.Empty;

			int paddedLength = Pad ((int) length);
			Require (paddedLength);
			int textLength = (int) length;
			if (textLength > 0 && data [offset + textLength - 1] == 0)
				textLength--;

			string value = Encoding.UTF8.GetString (data, offset, textLength);
			offset += paddedLength;
			return value;
		}

		public byte [] ReadArray ()
		{
			uint length = ReadUInt32 ();
			int paddedLength = Pad ((int) length);
			Require (paddedLength);
			byte [] value = new byte [length];
			Buffer.BlockCopy (data, offset, value, 0, (int) length);
			offset += paddedLength;
			return value;
		}

		static int Pad (int value)
		{
			return (value + 3) & ~3;
		}

		void Require (int count)
		{
			if (count < 0 || offset + count > data.Length)
				throw new EndOfStreamException ("Wayland message ended before the argument was complete.");
		}
	}

	internal sealed class WaylandRequestBuilder {
		readonly MemoryStream payload = new MemoryStream ();

		public void WriteInt32 (int value)
		{
			WriteUInt32 (unchecked ((uint) value));
		}

		public void WriteUInt32 (uint value)
		{
			payload.WriteByte ((byte) (value & 0xff));
			payload.WriteByte ((byte) ((value >> 8) & 0xff));
			payload.WriteByte ((byte) ((value >> 16) & 0xff));
			payload.WriteByte ((byte) ((value >> 24) & 0xff));
		}

		public void WriteObject (uint objectId)
		{
			WriteUInt32 (objectId);
		}

		public void WriteNewId (uint objectId)
		{
			WriteUInt32 (objectId);
		}

		public void WriteString (string value)
		{
			if (value == null)
				value = String.Empty;

			byte [] bytes = Encoding.UTF8.GetBytes (value);
			int wireLength = bytes.Length + 1;
			WriteUInt32 ((uint) wireLength);
			payload.Write (bytes, 0, bytes.Length);
			payload.WriteByte (0);
			WritePadding (wireLength);
		}

		public void WriteArray (byte [] value)
		{
			if (value == null)
				value = new byte [0];

			WriteUInt32 ((uint) value.Length);
			payload.Write (value, 0, value.Length);
			WritePadding (value.Length);
		}

		public byte [] ToArray (uint objectId, ushort opcode)
		{
			byte [] body = payload.ToArray ();
			int size = 8 + body.Length;
			if (size > UInt16.MaxValue)
				throw new InvalidOperationException ("Wayland message is too large.");

			byte [] message = new byte [size];
			WriteUInt32 (message, 0, objectId);
			WriteUInt32 (message, 4, ((uint) size << 16) | opcode);
			Buffer.BlockCopy (body, 0, message, 8, body.Length);
			return message;
		}

		void WritePadding (int written)
		{
			int padding = ((written + 3) & ~3) - written;
			for (int i = 0; i < padding; i++)
				payload.WriteByte (0);
		}

		static void WriteUInt32 (byte [] buffer, int offset, uint value)
		{
			buffer [offset] = (byte) (value & 0xff);
			buffer [offset + 1] = (byte) ((value >> 8) & 0xff);
			buffer [offset + 2] = (byte) ((value >> 16) & 0xff);
			buffer [offset + 3] = (byte) ((value >> 24) & 0xff);
		}
	}
}
