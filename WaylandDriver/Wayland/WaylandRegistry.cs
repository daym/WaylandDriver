using System;
using System.Collections.Generic;

namespace WaylandDriver.Wayland {
	internal sealed class WaylandGlobal {
		public readonly uint Name;
		public readonly string Interface;
		public readonly uint Version;

		public WaylandGlobal (uint name, string iface, uint version)
		{
			Name = name;
			Interface = iface;
			Version = version;
		}
	}

	internal sealed class WaylandRegistry {
		readonly List<WaylandGlobal> globals = new List<WaylandGlobal> ();

		public uint ObjectId { get; private set; }

		public WaylandRegistry (uint objectId)
		{
			ObjectId = objectId;
		}

		public IEnumerable<WaylandGlobal> Globals {
			get { return globals; }
		}

		public void HandleEvent (WaylandMessage message)
		{
			WaylandMessageReader reader = new WaylandMessageReader (message.Payload);

			if (message.Opcode == WaylandProtocol.WlRegistry.Global) {
				uint name = reader.ReadUInt32 ();
				string iface = reader.ReadString ();
				uint version = reader.ReadUInt32 ();
				globals.Add (new WaylandGlobal (name, iface, version));
				return;
			}

			if (message.Opcode == WaylandProtocol.WlRegistry.GlobalRemove) {
				uint name = reader.ReadUInt32 ();
				for (int i = globals.Count - 1; i >= 0; i--) {
					if (globals [i].Name == name)
						globals.RemoveAt (i);
				}
			}
		}

		public WaylandGlobal Find (string iface)
		{
			WaylandGlobal best = null;
			foreach (WaylandGlobal global in globals) {
				if (global.Interface != iface)
					continue;
				if (best == null || global.Version > best.Version)
					best = global;
			}
			return best;
		}
	}
}
