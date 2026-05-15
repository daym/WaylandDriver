using System;
using System.Collections.Generic;
using System.Text;

namespace WaylandDriver.Wayland {
	internal sealed class WaylandProtocolTrace {
		const int MaxRawBytes = 96;
		readonly object sync = new object ();
		readonly Dictionary<uint, string> objectInterfaces = new Dictionary<uint, string> ();

		WaylandProtocolTrace ()
		{
			objectInterfaces [1] = "wl_display";
		}

		public static WaylandProtocolTrace CreateFromEnvironment ()
		{
			if (!Enabled (Environment.GetEnvironmentVariable ("WAYLAND_DEBUG")) &&
			    !Enabled (Environment.GetEnvironmentVariable ("MONO_WAYLAND_DEBUG_PROTOCOL")))
				return null;

			return new WaylandProtocolTrace ();
		}

		static bool Enabled (string value)
		{
			if (String.IsNullOrEmpty (value))
				return false;

			return value != "0" &&
				!String.Equals (value, "false", StringComparison.OrdinalIgnoreCase) &&
				!String.Equals (value, "no", StringComparison.OrdinalIgnoreCase);
		}

		public void LogOutgoing (byte [] message, int fdCount)
		{
			if (message == null || message.Length < 8)
				return;

			uint objectId = ReadUInt32 (message, 0);
			uint header = ReadUInt32 (message, 4);
			ushort opcode = (ushort) (header & 0xffff);
			byte [] payload = SlicePayload (message);

			lock (sync) {
				string iface = GetInterface (objectId);
				string name = RequestName (iface, opcode);
				WriteLine ("->", iface, objectId, name, FormatArguments (iface, name, payload, false), fdCount);
				TrackOutgoingSideEffects (iface, opcode, payload, objectId);
			}
		}

		public void LogIncoming (WaylandMessage message, int pendingFdCount)
		{
			if (message == null)
				return;

			lock (sync) {
				string iface = GetInterface (message.ObjectId);
				string name = EventName (iface, message.Opcode);
				WriteLine ("<-", iface, message.ObjectId, name, FormatArguments (iface, name, message.Payload, true), pendingFdCount);
				TrackIncomingSideEffects (iface, message.Opcode, message.Payload, message.ObjectId);
			}
		}

		void WriteLine (string direction, string iface, uint objectId, string name, string args, int fdCount)
		{
			StringBuilder line = new StringBuilder ();
			line.Append ("WAYLAND ");
			line.Append (direction);
			line.Append (' ');
			line.Append (iface);
			line.Append ('@');
			line.Append (objectId);
			line.Append ('.');
			line.Append (name);
			line.Append ('(');
			line.Append (args);
			line.Append (')');
			if (fdCount > 0) {
				line.Append (" fds=");
				line.Append (fdCount);
			}
			Console.Error.WriteLine (line.ToString ());
		}

		string GetInterface (uint objectId)
		{
			string iface;
			if (objectInterfaces.TryGetValue (objectId, out iface))
				return iface;
			return "object";
		}

		string ObjectName (uint objectId)
		{
			if (objectId == 0)
				return "null";
			return GetInterface (objectId) + "@" + objectId.ToString ();
		}

		void RegisterObject (uint objectId, string iface)
		{
			if (objectId != 0 && !String.IsNullOrEmpty (iface))
				objectInterfaces [objectId] = iface;
		}

		void TrackOutgoingSideEffects (string iface, ushort opcode, byte [] payload, uint objectId)
		{
			WaylandPayloadReader reader = new WaylandPayloadReader (payload);

			if (iface == "wl_display") {
				if (opcode == WaylandProtocol.WlDisplay.Sync && reader.TryReadUInt32 (out objectId))
					RegisterObject (objectId, "wl_callback");
				else if (opcode == WaylandProtocol.WlDisplay.GetRegistry && reader.TryReadUInt32 (out objectId))
					RegisterObject (objectId, "wl_registry");
				return;
			}

			if (iface == "wl_registry" && opcode == WaylandProtocol.WlRegistry.Bind) {
				uint name;
				string boundInterface;
				uint version;
				uint newId;
				if (reader.TryReadUInt32 (out name) &&
				    reader.TryReadString (out boundInterface) &&
				    reader.TryReadUInt32 (out version) &&
				    reader.TryReadUInt32 (out newId))
					RegisterObject (newId, boundInterface);
				return;
			}

			if (iface == "wl_compositor" && opcode == WaylandProtocol.WlCompositor.CreateSurface && reader.TryReadUInt32 (out objectId)) {
				RegisterObject (objectId, "wl_surface");
				return;
			}

			if (iface == "wl_subcompositor" && opcode == WaylandProtocol.WlSubcompositor.GetSubsurface && reader.TryReadUInt32 (out objectId)) {
				RegisterObject (objectId, "wl_subsurface");
				return;
			}

			if (iface == "wl_shm" && opcode == WaylandProtocol.WlShm.CreatePool && reader.TryReadUInt32 (out objectId)) {
				RegisterObject (objectId, "wl_shm_pool");
				return;
			}

			if (iface == "wl_shm_pool" && opcode == WaylandProtocol.WlShmPool.CreateBuffer && reader.TryReadUInt32 (out objectId)) {
				RegisterObject (objectId, "wl_buffer");
				return;
			}

			if (iface == "wl_seat") {
				if (opcode == WaylandProtocol.WlSeat.GetPointer && reader.TryReadUInt32 (out objectId))
					RegisterObject (objectId, "wl_pointer");
				else if (opcode == WaylandProtocol.WlSeat.GetKeyboard && reader.TryReadUInt32 (out objectId))
					RegisterObject (objectId, "wl_keyboard");
				return;
			}

			if (iface == "wl_data_device_manager") {
				if (opcode == WaylandProtocol.WlDataDeviceManager.CreateDataSource && reader.TryReadUInt32 (out objectId))
					RegisterObject (objectId, "wl_data_source");
				else if (opcode == WaylandProtocol.WlDataDeviceManager.GetDataDevice && reader.TryReadUInt32 (out objectId))
					RegisterObject (objectId, "wl_data_device");
				return;
			}

			if (iface == "wp_cursor_shape_manager_v1" && opcode == WaylandProtocol.WpCursorShapeManagerV1.GetPointer && reader.TryReadUInt32 (out objectId)) {
				RegisterObject (objectId, "wp_cursor_shape_device_v1");
				return;
			}

			if (iface == "xdg_wm_base") {
				if (opcode == WaylandProtocol.XdgWmBase.CreatePositioner && reader.TryReadUInt32 (out objectId))
					RegisterObject (objectId, "xdg_positioner");
				else if (opcode == WaylandProtocol.XdgWmBase.GetXdgSurface && reader.TryReadUInt32 (out objectId))
					RegisterObject (objectId, "xdg_surface");
				return;
			}

			if (iface == "xdg_surface") {
				if (opcode == WaylandProtocol.XdgSurface.GetToplevel && reader.TryReadUInt32 (out objectId))
					RegisterObject (objectId, "xdg_toplevel");
				else if (opcode == WaylandProtocol.XdgSurface.GetPopup && reader.TryReadUInt32 (out objectId))
					RegisterObject (objectId, "xdg_popup");
				return;
			}

			if (IsDestroyRequest (iface, opcode))
				objectInterfaces.Remove (objectId);
		}

		void TrackIncomingSideEffects (string iface, ushort opcode, byte [] payload, uint objectId)
		{
			WaylandPayloadReader reader = new WaylandPayloadReader (payload);

			if (iface == "wl_display" && opcode == WaylandProtocol.WlDisplay.DeleteId) {
				uint deletedId;
				if (reader.TryReadUInt32 (out deletedId))
					objectInterfaces.Remove (deletedId);
				return;
			}

			if (iface == "wl_data_device" && opcode == WaylandProtocol.WlDataDevice.DataOffer) {
				uint newId;
				if (reader.TryReadUInt32 (out newId))
					RegisterObject (newId, "wl_data_offer");
				return;
			}

			if (iface == "wl_callback" && opcode == WaylandProtocol.WlCallback.Done)
				objectInterfaces.Remove (objectId);
		}

		static bool IsDestroyRequest (string iface, ushort opcode)
		{
			if (opcode != 0)
				return false;

			return iface == "wl_subcompositor" ||
				iface == "wl_subsurface" ||
				iface == "wl_shm_pool" ||
				iface == "wl_buffer" ||
				iface == "wl_data_offer" ||
				iface == "wl_data_source" ||
				iface == "wp_cursor_shape_manager_v1" ||
				iface == "wp_cursor_shape_device_v1" ||
				iface == "xdg_positioner" ||
				iface == "xdg_surface" ||
				iface == "xdg_toplevel" ||
				iface == "xdg_popup";
		}

		string FormatArguments (string iface, string name, byte [] payload, bool incoming)
		{
			WaylandPayloadReader reader = new WaylandPayloadReader (payload);
			uint a, b, c;
			int ia, ib, ic, id;
			string text;

			if (iface == "wl_registry" && name == "bind" &&
			    reader.TryReadUInt32 (out a) &&
			    reader.TryReadString (out text) &&
			    reader.TryReadUInt32 (out b) &&
			    reader.TryReadUInt32 (out c))
				return "name=" + a.ToString () + ", interface=\"" + text + "\", version=" + b.ToString () + ", id=" + text + "@" + c.ToString ();

			if (name == "set_title" || name == "set_app_id" || name == "offer") {
				if (reader.TryReadString (out text))
					return "\"" + Escape (text) + "\"";
			}

			if ((name == "set_min_size" || name == "set_max_size" || name == "set_size") &&
			    reader.TryReadInt32 (out ia) &&
			    reader.TryReadInt32 (out ib))
				return ia.ToString () + ", " + ib.ToString ();

			if (name == "set_anchor_rect" &&
			    reader.TryReadInt32 (out ia) &&
			    reader.TryReadInt32 (out ib) &&
			    reader.TryReadInt32 (out ic) &&
			    reader.TryReadInt32 (out id))
				return ia.ToString () + ", " + ib.ToString () + ", " + ic.ToString () + ", " + id.ToString ();

			if (name == "set_offset" &&
			    reader.TryReadInt32 (out ia) &&
			    reader.TryReadInt32 (out ib))
				return ia.ToString () + ", " + ib.ToString ();

			if (iface == "xdg_toplevel" && name == "set_parent" && reader.TryReadUInt32 (out a))
				return ObjectName (a);

			if (iface == "xdg_toplevel" && name == "configure" &&
			    reader.TryReadInt32 (out ia) &&
			    reader.TryReadInt32 (out ib)) {
				byte [] states;
				if (reader.TryReadArray (out states))
					return "width=" + ia.ToString () + ", height=" + ib.ToString () + ", states=" + FormatRawBytes (states);
				return "width=" + ia.ToString () + ", height=" + ib.ToString ();
			}

			if (iface == "xdg_popup" && name == "configure" &&
			    reader.TryReadInt32 (out ia) &&
			    reader.TryReadInt32 (out ib) &&
			    reader.TryReadInt32 (out ic) &&
			    reader.TryReadInt32 (out id))
				return "x=" + ia.ToString () + ", y=" + ib.ToString () + ", width=" + ic.ToString () + ", height=" + id.ToString ();

			if (name == "configure" && reader.TryReadUInt32 (out a))
				return "serial=" + a.ToString ();

			if (name == "ack_configure" || name == "pong" || name == "done" || name == "ping" || name == "repositioned") {
				if (reader.TryReadUInt32 (out a))
					return a.ToString ();
			}

			if (name == "create_surface" ||
			    name == "create_pool" ||
			    name == "get_registry" ||
			    name == "sync" ||
			    name == "get_pointer" ||
			    name == "get_keyboard" ||
			    name == "create_data_source" ||
			    name == "create_positioner" ||
			    name == "get_toplevel") {
				if (reader.TryReadUInt32 (out a))
					return "id=" + ObjectName (a);
			}

			if (name == "get_xdg_surface" &&
			    reader.TryReadUInt32 (out a) &&
			    reader.TryReadUInt32 (out b))
				return "id=" + ObjectName (a) + ", surface=" + ObjectName (b);

			if (name == "get_subsurface" &&
			    reader.TryReadUInt32 (out a) &&
			    reader.TryReadUInt32 (out b) &&
			    reader.TryReadUInt32 (out c))
				return "id=" + ObjectName (a) + ", surface=" + ObjectName (b) + ", parent=" + ObjectName (c);

			if (name == "get_popup" &&
			    reader.TryReadUInt32 (out a) &&
			    reader.TryReadUInt32 (out b) &&
			    reader.TryReadUInt32 (out c))
				return "id=" + ObjectName (a) + ", parent=" + ObjectName (b) + ", positioner=" + ObjectName (c);

			if (name == "create_buffer" &&
			    reader.TryReadUInt32 (out a) &&
			    reader.TryReadInt32 (out ia) &&
			    reader.TryReadInt32 (out ib) &&
			    reader.TryReadInt32 (out ic) &&
			    reader.TryReadInt32 (out id) &&
			    reader.TryReadUInt32 (out b))
				return "id=" + ObjectName (a) + ", offset=" + ia.ToString () + ", width=" + ib.ToString () + ", height=" + ic.ToString () + ", stride=" + id.ToString () + ", format=" + b.ToString ();

			if (name == "attach" &&
			    reader.TryReadUInt32 (out a) &&
			    reader.TryReadInt32 (out ia) &&
			    reader.TryReadInt32 (out ib))
				return "buffer=" + ObjectName (a) + ", x=" + ia.ToString () + ", y=" + ib.ToString ();

			if ((name == "damage" || name == "damage_buffer") &&
			    reader.TryReadInt32 (out ia) &&
			    reader.TryReadInt32 (out ib) &&
			    reader.TryReadInt32 (out ic) &&
			    reader.TryReadInt32 (out id))
				return ia.ToString () + ", " + ib.ToString () + ", " + ic.ToString () + ", " + id.ToString ();

			if (name == "set_buffer_scale" && reader.TryReadInt32 (out ia))
				return ia.ToString ();

			if (name == "place_above" || name == "place_below" || name == "enter" || name == "leave") {
				if (reader.TryReadUInt32 (out a))
					return ObjectName (a);
			}

			if (name == "error" &&
			    reader.TryReadUInt32 (out a) &&
			    reader.TryReadUInt32 (out b) &&
			    reader.TryReadString (out text))
				return "object=" + ObjectName (a) + ", code=" + b.ToString () + ", message=\"" + Escape (text) + "\"";

			if (payload == null || payload.Length == 0)
				return String.Empty;

			return "raw=" + FormatRawBytes (payload);
		}

		static string RequestName (string iface, ushort opcode)
		{
			switch (iface) {
			case "wl_display":
				if (opcode == WaylandProtocol.WlDisplay.Sync)
					return "sync";
				if (opcode == WaylandProtocol.WlDisplay.GetRegistry)
					return "get_registry";
				break;
			case "wl_registry":
				if (opcode == WaylandProtocol.WlRegistry.Bind)
					return "bind";
				break;
			case "wl_compositor":
				if (opcode == WaylandProtocol.WlCompositor.CreateSurface)
					return "create_surface";
				break;
			case "wl_subcompositor":
				if (opcode == WaylandProtocol.WlSubcompositor.Destroy)
					return "destroy";
				if (opcode == WaylandProtocol.WlSubcompositor.GetSubsurface)
					return "get_subsurface";
				break;
			case "wl_subsurface":
				if (opcode == WaylandProtocol.WlSubsurface.Destroy)
					return "destroy";
				if (opcode == WaylandProtocol.WlSubsurface.SetPosition)
					return "set_position";
				if (opcode == WaylandProtocol.WlSubsurface.PlaceAbove)
					return "place_above";
				if (opcode == WaylandProtocol.WlSubsurface.PlaceBelow)
					return "place_below";
				if (opcode == WaylandProtocol.WlSubsurface.SetSync)
					return "set_sync";
				if (opcode == WaylandProtocol.WlSubsurface.SetDesync)
					return "set_desync";
				break;
			case "wl_shm":
				if (opcode == WaylandProtocol.WlShm.CreatePool)
					return "create_pool";
				break;
			case "wl_shm_pool":
				if (opcode == WaylandProtocol.WlShmPool.CreateBuffer)
					return "create_buffer";
				if (opcode == WaylandProtocol.WlShmPool.Destroy)
					return "destroy";
				break;
			case "wl_buffer":
				if (opcode == WaylandProtocol.WlBuffer.Destroy)
					return "destroy";
				break;
			case "wl_surface":
				if (opcode == WaylandProtocol.WlSurface.Destroy)
					return "destroy";
				if (opcode == WaylandProtocol.WlSurface.Attach)
					return "attach";
				if (opcode == WaylandProtocol.WlSurface.Damage)
					return "damage";
				if (opcode == WaylandProtocol.WlSurface.Commit)
					return "commit";
				if (opcode == WaylandProtocol.WlSurface.SetBufferScale)
					return "set_buffer_scale";
				if (opcode == WaylandProtocol.WlSurface.DamageBuffer)
					return "damage_buffer";
				break;
			case "wl_seat":
				if (opcode == WaylandProtocol.WlSeat.GetPointer)
					return "get_pointer";
				if (opcode == WaylandProtocol.WlSeat.GetKeyboard)
					return "get_keyboard";
				break;
			case "wl_pointer":
				if (opcode == WaylandProtocol.WlPointer.SetCursor)
					return "set_cursor";
				if (opcode == WaylandProtocol.WlPointer.Release)
					return "release";
				break;
			case "wl_keyboard":
				if (opcode == WaylandProtocol.WlKeyboard.Release)
					return "release";
				break;
			case "wl_data_offer":
				if (opcode == WaylandProtocol.WlDataOffer.Accept)
					return "accept";
				if (opcode == WaylandProtocol.WlDataOffer.Receive)
					return "receive";
				if (opcode == WaylandProtocol.WlDataOffer.Destroy)
					return "destroy";
				break;
			case "wl_data_source":
				if (opcode == WaylandProtocol.WlDataSource.Offer)
					return "offer";
				if (opcode == WaylandProtocol.WlDataSource.Destroy)
					return "destroy";
				break;
			case "wl_data_device":
				if (opcode == WaylandProtocol.WlDataDevice.SetSelection)
					return "set_selection";
				break;
			case "wl_data_device_manager":
				if (opcode == WaylandProtocol.WlDataDeviceManager.CreateDataSource)
					return "create_data_source";
				if (opcode == WaylandProtocol.WlDataDeviceManager.GetDataDevice)
					return "get_data_device";
				break;
			case "wp_cursor_shape_manager_v1":
				if (opcode == WaylandProtocol.WpCursorShapeManagerV1.Destroy)
					return "destroy";
				if (opcode == WaylandProtocol.WpCursorShapeManagerV1.GetPointer)
					return "get_pointer";
				break;
			case "wp_cursor_shape_device_v1":
				if (opcode == WaylandProtocol.WpCursorShapeDeviceV1.Destroy)
					return "destroy";
				if (opcode == WaylandProtocol.WpCursorShapeDeviceV1.SetShape)
					return "set_shape";
				break;
			case "xdg_wm_base":
				if (opcode == WaylandProtocol.XdgWmBase.Destroy)
					return "destroy";
				if (opcode == WaylandProtocol.XdgWmBase.CreatePositioner)
					return "create_positioner";
				if (opcode == WaylandProtocol.XdgWmBase.GetXdgSurface)
					return "get_xdg_surface";
				if (opcode == WaylandProtocol.XdgWmBase.Pong)
					return "pong";
				break;
			case "xdg_positioner":
				if (opcode == WaylandProtocol.XdgPositioner.Destroy)
					return "destroy";
				if (opcode == WaylandProtocol.XdgPositioner.SetSize)
					return "set_size";
				if (opcode == WaylandProtocol.XdgPositioner.SetAnchorRect)
					return "set_anchor_rect";
				if (opcode == WaylandProtocol.XdgPositioner.SetAnchor)
					return "set_anchor";
				if (opcode == WaylandProtocol.XdgPositioner.SetGravity)
					return "set_gravity";
				if (opcode == WaylandProtocol.XdgPositioner.SetConstraintAdjustment)
					return "set_constraint_adjustment";
				if (opcode == WaylandProtocol.XdgPositioner.SetOffset)
					return "set_offset";
				break;
			case "xdg_surface":
				if (opcode == WaylandProtocol.XdgSurface.Destroy)
					return "destroy";
				if (opcode == WaylandProtocol.XdgSurface.GetToplevel)
					return "get_toplevel";
				if (opcode == WaylandProtocol.XdgSurface.GetPopup)
					return "get_popup";
				if (opcode == WaylandProtocol.XdgSurface.AckConfigure)
					return "ack_configure";
				break;
			case "xdg_toplevel":
				if (opcode == WaylandProtocol.XdgToplevel.Destroy)
					return "destroy";
				if (opcode == WaylandProtocol.XdgToplevel.SetParent)
					return "set_parent";
				if (opcode == WaylandProtocol.XdgToplevel.SetTitle)
					return "set_title";
				if (opcode == WaylandProtocol.XdgToplevel.SetAppId)
					return "set_app_id";
				if (opcode == WaylandProtocol.XdgToplevel.SetMaxSize)
					return "set_max_size";
				if (opcode == WaylandProtocol.XdgToplevel.SetMinSize)
					return "set_min_size";
				if (opcode == WaylandProtocol.XdgToplevel.SetMaximized)
					return "set_maximized";
				if (opcode == WaylandProtocol.XdgToplevel.UnsetMaximized)
					return "unset_maximized";
				if (opcode == WaylandProtocol.XdgToplevel.SetMinimized)
					return "set_minimized";
				break;
			case "xdg_popup":
				if (opcode == WaylandProtocol.XdgPopup.Destroy)
					return "destroy";
				if (opcode == WaylandProtocol.XdgPopup.Grab)
					return "grab";
				break;
			}

			return "opcode" + opcode.ToString ();
		}

		static string EventName (string iface, ushort opcode)
		{
			switch (iface) {
			case "wl_display":
				if (opcode == WaylandProtocol.WlDisplay.Error)
					return "error";
				if (opcode == WaylandProtocol.WlDisplay.DeleteId)
					return "delete_id";
				break;
			case "wl_registry":
				if (opcode == WaylandProtocol.WlRegistry.Global)
					return "global";
				if (opcode == WaylandProtocol.WlRegistry.GlobalRemove)
					return "global_remove";
				break;
			case "wl_callback":
				if (opcode == WaylandProtocol.WlCallback.Done)
					return "done";
				break;
			case "wl_buffer":
				if (opcode == WaylandProtocol.WlBuffer.Release)
					return "release";
				break;
			case "wl_surface":
				if (opcode == WaylandProtocol.WlSurface.Enter)
					return "enter";
				if (opcode == WaylandProtocol.WlSurface.Leave)
					return "leave";
				break;
			case "wl_output":
				if (opcode == WaylandProtocol.WlOutput.Geometry)
					return "geometry";
				if (opcode == WaylandProtocol.WlOutput.Mode)
					return "mode";
				if (opcode == WaylandProtocol.WlOutput.Done)
					return "done";
				if (opcode == WaylandProtocol.WlOutput.Scale)
					return "scale";
				break;
			case "wl_seat":
				if (opcode == WaylandProtocol.WlSeat.Capabilities)
					return "capabilities";
				if (opcode == WaylandProtocol.WlSeat.Name)
					return "name";
				break;
			case "wl_pointer":
				if (opcode == WaylandProtocol.WlPointer.Enter)
					return "enter";
				if (opcode == WaylandProtocol.WlPointer.Leave)
					return "leave";
				if (opcode == WaylandProtocol.WlPointer.Motion)
					return "motion";
				if (opcode == WaylandProtocol.WlPointer.Button)
					return "button";
				if (opcode == WaylandProtocol.WlPointer.Axis)
					return "axis";
				if (opcode == WaylandProtocol.WlPointer.Frame)
					return "frame";
				if (opcode == WaylandProtocol.WlPointer.AxisSource)
					return "axis_source";
				if (opcode == WaylandProtocol.WlPointer.AxisStop)
					return "axis_stop";
				if (opcode == WaylandProtocol.WlPointer.AxisDiscrete)
					return "axis_discrete";
				break;
			case "wl_keyboard":
				if (opcode == WaylandProtocol.WlKeyboard.Keymap)
					return "keymap";
				if (opcode == WaylandProtocol.WlKeyboard.Enter)
					return "enter";
				if (opcode == WaylandProtocol.WlKeyboard.Leave)
					return "leave";
				if (opcode == WaylandProtocol.WlKeyboard.Key)
					return "key";
				if (opcode == WaylandProtocol.WlKeyboard.Modifiers)
					return "modifiers";
				if (opcode == WaylandProtocol.WlKeyboard.RepeatInfo)
					return "repeat_info";
				break;
			case "wl_data_offer":
				if (opcode == WaylandProtocol.WlDataOffer.Offer)
					return "offer";
				break;
			case "wl_data_source":
				if (opcode == WaylandProtocol.WlDataSource.Target)
					return "target";
				if (opcode == WaylandProtocol.WlDataSource.Send)
					return "send";
				if (opcode == WaylandProtocol.WlDataSource.Cancelled)
					return "cancelled";
				break;
			case "wl_data_device":
				if (opcode == WaylandProtocol.WlDataDevice.DataOffer)
					return "data_offer";
				if (opcode == WaylandProtocol.WlDataDevice.Selection)
					return "selection";
				break;
			case "xdg_wm_base":
				if (opcode == WaylandProtocol.XdgWmBase.Ping)
					return "ping";
				break;
			case "xdg_surface":
				if (opcode == WaylandProtocol.XdgSurface.Configure)
					return "configure";
				break;
			case "xdg_toplevel":
				if (opcode == WaylandProtocol.XdgToplevel.Configure)
					return "configure";
				if (opcode == WaylandProtocol.XdgToplevel.Close)
					return "close";
				break;
			case "xdg_popup":
				if (opcode == WaylandProtocol.XdgPopup.Configure)
					return "configure";
				if (opcode == WaylandProtocol.XdgPopup.PopupDone)
					return "popup_done";
				if (opcode == WaylandProtocol.XdgPopup.Repositioned)
					return "repositioned";
				break;
			}

			return "opcode" + opcode.ToString ();
		}

		static byte [] SlicePayload (byte [] message)
		{
			byte [] payload = new byte [message.Length - 8];
			Buffer.BlockCopy (message, 8, payload, 0, payload.Length);
			return payload;
		}

		static uint ReadUInt32 (byte [] data, int offset)
		{
			return (uint) data [offset] |
				((uint) data [offset + 1] << 8) |
				((uint) data [offset + 2] << 16) |
				((uint) data [offset + 3] << 24);
		}

		static string Escape (string value)
		{
			return value.Replace ("\\", "\\\\").Replace ("\"", "\\\"");
		}

		static string FormatRawBytes (byte [] data)
		{
			if (data == null || data.Length == 0)
				return "";

			int count = Math.Min (data.Length, MaxRawBytes);
			StringBuilder builder = new StringBuilder ();
			for (int i = 0; i < count; i++) {
				if (i != 0)
					builder.Append (' ');
				builder.Append (data [i].ToString ("x2"));
			}
			if (count < data.Length)
				builder.Append (" ...");
			return builder.ToString ();
		}

		sealed class WaylandPayloadReader {
			readonly byte [] data;
			int offset;

			public WaylandPayloadReader (byte [] data)
			{
				this.data = data ?? new byte [0];
			}

			public bool TryReadInt32 (out int value)
			{
				uint unsigned;
				if (!TryReadUInt32 (out unsigned)) {
					value = 0;
					return false;
				}
				value = unchecked ((int) unsigned);
				return true;
			}

			public bool TryReadUInt32 (out uint value)
			{
				if (offset + 4 > data.Length) {
					value = 0;
					return false;
				}

				value = (uint) data [offset] |
					((uint) data [offset + 1] << 8) |
					((uint) data [offset + 2] << 16) |
					((uint) data [offset + 3] << 24);
				offset += 4;
				return true;
			}

			public bool TryReadString (out string value)
			{
				uint length;
				if (!TryReadUInt32 (out length)) {
					value = String.Empty;
					return false;
				}

				int paddedLength = Pad ((int) length);
				if (offset + paddedLength > data.Length) {
					value = String.Empty;
					return false;
				}

				int textLength = (int) length;
				if (textLength > 0 && data [offset + textLength - 1] == 0)
					textLength--;
				value = Encoding.UTF8.GetString (data, offset, textLength);
				offset += paddedLength;
				return true;
			}

			public bool TryReadArray (out byte [] value)
			{
				uint length;
				if (!TryReadUInt32 (out length)) {
					value = new byte [0];
					return false;
				}

				int paddedLength = Pad ((int) length);
				if (offset + paddedLength > data.Length) {
					value = new byte [0];
					return false;
				}

				value = new byte [length];
				Buffer.BlockCopy (data, offset, value, 0, (int) length);
				offset += paddedLength;
				return true;
			}

			static int Pad (int value)
			{
				return (value + 3) & ~3;
			}
		}
	}
}
