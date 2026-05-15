using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
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

		sealed class WaylandDataOffer {
			public uint ObjectId;
			public readonly List<string> MimeTypes = new List<string> ();
		}

		sealed class ClipboardSelection {
			public readonly Dictionary<int, object> Data = new Dictionary<int, object> ();
			public bool Dirty;
			public bool OwnsSelection;
			public uint SourceId;
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
					if (bytes.Length > 0) {
						// wl_keyboard.keymap is specified as a read-only mmap.
						// This avoids depending on SCM_RIGHTS open-file-description
						// seek state and matches the protocol's MAP_PRIVATE rule.
						IntPtr mapping = Syscall.mmap (IntPtr.Zero, (ulong) bytes.Length, MmapProts.PROT_READ, MmapFlags.MAP_PRIVATE, fd, 0);
						if (mapping == new IntPtr (-1))
							UnixMarshal.ThrowExceptionForLastError ();
						try {
							Marshal.Copy (mapping, bytes, 0, bytes.Length);
						} finally {
							Syscall.munmap (mapping, (ulong) bytes.Length);
						}
					}

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
			public Keys ShortcutModifiers;
			public bool HasShortcutModifiers;
		}

		interface IWaylandKeyboardLayout : IDisposable {
			Keys ModifierKeys { get; }
			void SetModifiers (uint depressed, uint latched, uint locked, uint group);
			WaylandKeyResult TranslateKey (uint evdevKey, bool pressed);
		}

		const uint EvdevKeyLeftShift = 42;
		const uint EvdevKeyRightShift = 54;
		const uint EvdevKeyLeftControl = 29;
		const uint EvdevKeyRightControl = 97;
		const uint EvdevKeyLeftAlt = 56;
		const uint EvdevKeyRightAlt = 100;
		const uint XkbKeycodeOffset = 8;
		const uint XkbModifierShiftMask = 1u << 0;
		const uint XkbModifierLockMask = 1u << 1;
		const uint XkbModifierControlMask = 1u << 2;
		const uint XkbModifierMod1Mask = 1u << 3;
		const uint XkbModifierMod2Mask = 1u << 4;
		const uint XkbModifierMod3Mask = 1u << 5;
		const uint XkbModifierMod4Mask = 1u << 6;
		const uint XkbModifierMod5Mask = 1u << 7;
		const uint XkbAllRealModifierMask = XkbModifierShiftMask | XkbModifierLockMask | XkbModifierControlMask |
			XkbModifierMod1Mask | XkbModifierMod2Mask | XkbModifierMod3Mask | XkbModifierMod4Mask | XkbModifierMod5Mask;
		const uint UnicodeMaxCodePoint = 0x10ffff;
		const uint XkbKeysymUnicodePrefix = 0x01000000;
		const uint XkbKeysymUnicodePrefixMask = 0xff000000;
		const uint XkbKeysymUnicodeCodepointMask = 0x00ffffff;
		const uint XkbKeysymIsoSpecialStart = 0xfe00;
		const uint XkbKeysymSpecialStart = 0xff00;
		const uint XkbKeysymSpecialEnd = 0xffff;
		const uint XkbKeysymBackSpace = 0xff08;
		const uint XkbKeysymTab = 0xff09;
		const uint XkbKeysymReturn = 0xff0d;
		const uint XkbKeysymPause = 0xff13;
		const uint XkbKeysymScrollLock = 0xff14;
		const uint XkbKeysymEscape = 0xff1b;
		const uint XkbKeysymHome = 0xff50;
		const uint XkbKeysymLeft = 0xff51;
		const uint XkbKeysymUp = 0xff52;
		const uint XkbKeysymRight = 0xff53;
		const uint XkbKeysymDown = 0xff54;
		const uint XkbKeysymPageUp = 0xff55;
		const uint XkbKeysymPageDown = 0xff56;
		const uint XkbKeysymEnd = 0xff57;
		const uint XkbKeysymPrint = 0xff61;
		const uint XkbKeysymInsert = 0xff63;
		const uint XkbKeysymMenu = 0xff67;
		const uint XkbKeysymHelp = 0xff6a;
		const uint XkbKeysymBreak = 0xff6b;
		const uint XkbKeysymModeSwitch = 0xff7e;
		const uint XkbKeysymNumLock = 0xff7f;
		const uint XkbKeysymKpEnter = 0xff8d;
		const uint XkbKeysymKpMultiply = 0xffaa;
		const uint XkbKeysymKpAdd = 0xffab;
		const uint XkbKeysymKpSeparator = 0xffac;
		const uint XkbKeysymKpSubtract = 0xffad;
		const uint XkbKeysymKpDecimal = 0xffae;
		const uint XkbKeysymKpDivide = 0xffaf;
		const uint XkbKeysymKp0 = 0xffb0;
		const uint XkbKeysymKp9 = 0xffb9;
		const uint XkbKeysymF1 = 0xffbe;
		const uint XkbKeysymF12 = 0xffc9;
		const uint XkbKeysymShiftL = 0xffe1;
		const uint XkbKeysymShiftR = 0xffe2;
		const uint XkbKeysymControlL = 0xffe3;
		const uint XkbKeysymControlR = 0xffe4;
		const uint XkbKeysymCapsLock = 0xffe5;
		const uint XkbKeysymMetaL = 0xffe7;
		const uint XkbKeysymMetaR = 0xffe8;
		const uint XkbKeysymAltL = 0xffe9;
		const uint XkbKeysymAltR = 0xffea;
		const uint XkbKeysymSuperL = 0xffeb;
		const uint XkbKeysymSuperR = 0xffec;
		const uint XkbKeysymHyperL = 0xffed;
		const uint XkbKeysymHyperR = 0xffee;
		const uint XkbKeysymDelete = 0xffff;
		const uint XkbKeysymIsoLevel3Shift = 0xfe03;
		const int ClipboardFormatText = 1;
		const int ClipboardFormatBitmap = 2;
		const int ClipboardFormatMetafilePict = 3;
		const int ClipboardFormatSymbolicLink = 4;
		const int ClipboardFormatDif = 5;
		const int ClipboardFormatTiff = 6;
		const int ClipboardFormatOemText = 7;
		const int ClipboardFormatDib = 8;
		const int ClipboardFormatPalette = 9;
		const int ClipboardFormatPenData = 10;
		const int ClipboardFormatRiff = 11;
		const int ClipboardFormatWaveAudio = 12;
		const int ClipboardFormatUnicodeText = 13;
		const int ClipboardFormatEnhancedMetafile = 14;
		const int ClipboardFormatFileDrop = 15;
		const int ClipboardFormatLocale = 16;
		const int ClipboardCustomFormatStart = 0xc000;
		const string MimeTextUtf8 = "text/plain;charset=utf-8";
		const string MimeTextPlain = "text/plain";
		const string MimeHtml = "text/html";
		const string MimeRtf = "text/rtf";
		static readonly IntPtr ClipboardHandle = new IntPtr (1);
		static readonly IntPtr PrimarySelectionHandle = new IntPtr (2);

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
				result.ShortcutModifiers = modifierKeys;
				result.HasShortcutModifiers = true;
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
				if (keysDown.Contains (EvdevKeyLeftShift) || keysDown.Contains (EvdevKeyRightShift))
					keys |= Keys.Shift;
				if (keysDown.Contains (EvdevKeyLeftControl) || keysDown.Contains (EvdevKeyRightControl))
					keys |= Keys.Control;
				// KEY_RIGHTALT is commonly AltGr/ISO_Level3_Shift on Wayland
				// layouts.  The physical fallback has no XKB type/action state,
				// so only KEY_LEFTALT is allowed to enter WinForms' system-Alt
				// path; otherwise an AltGr press/release can activate menu-mode
				// semantics and consume later printable input.
				if (keysDown.Contains (EvdevKeyLeftAlt))
					keys |= Keys.Alt;
				modifierKeys = keys;
			}
		}

		sealed class ManagedXkbKeyboardLayout : IWaylandKeyboardLayout {
			sealed class Key {
				public string Name;
				public KeyGroup [] Groups;
			}

			sealed class KeyGroup {
				public XkbSymbol [] Symbols;
				public XkbResolvedType Type;
			}

			sealed class ParsedKey {
				public XkbSymbol [][] Groups;
				public string [] TypeNames;
				public string [] VirtualModifierNames = EmptyStringArray;
			}

			struct XkbSymbol {
				public uint Keysym;
				public string Name;
				public string Text;
				public bool NoSymbol;
			}

			sealed class XkbType {
				public string Name;
				public string [] ModifierNames = EmptyStringArray;
				public readonly List<XkbTypeMap> Maps = new List<XkbTypeMap> ();
				public readonly List<XkbTypePreserve> Preserves = new List<XkbTypePreserve> ();
			}

			struct XkbTypeMap {
				public string [] ModifierNames;
				public int Level;
			}

			struct XkbTypePreserve {
				public string [] ModifierNames;
				public string [] PreserveNames;
			}

			enum InterpretPredicateOperation {
				AnyOfOrNone,
				AnyOf,
				Any,
				NoneOf,
				AllOf,
				Exactly
			}

			struct CompatInterpret {
				public string SymbolName;
				public string VirtualModifierName;
				public bool LevelOneOnly;
				public InterpretPredicateOperation PredicateOperation;
				public uint PredicateMask;
				public int Order;
			}

			sealed class XkbResolvedType {
				public string Name;
				public uint ModifierMask;
				public readonly Dictionary<uint, int> Levels = new Dictionary<uint, int> ();
				public readonly Dictionary<uint, uint> Preserves = new Dictionary<uint, uint> ();
			}

			struct ModifierMapMember {
				public bool IsKeyName;
				public string Name;
			}

			sealed class ModifierMapEntry {
				public uint Mask;
				public readonly List<ModifierMapMember> Members = new List<ModifierMapMember> ();
			}

			enum TokenKind {
				End,
				Identifier,
				Number,
				String,
				KeyName,
				Symbol
			}

			struct Token {
				public TokenKind Kind;
				public string Text;
				public uint Number;
				public char Symbol;
			}

			sealed class Parser {
				readonly XkbLexer lexer;
				readonly Dictionary<string, uint> keycodes = new Dictionary<string, uint> (StringComparer.Ordinal);
				readonly Dictionary<string, ParsedKey> symbols = new Dictionary<string, ParsedKey> (StringComparer.Ordinal);
				readonly Dictionary<string, XkbType> types = new Dictionary<string, XkbType> (StringComparer.Ordinal);
				readonly List<CompatInterpret> compatInterprets = new List<CompatInterpret> ();
				readonly List<ModifierMapEntry> modifierMaps = new List<ModifierMapEntry> ();
				readonly Dictionary<string, uint> explicitVirtualModifierMasks = new Dictionary<string, uint> (StringComparer.Ordinal);
				readonly string [] defaultSymbolTypeNames = new string [4];
				int nextInterpretOrder;
				string unsupportedDiagnostic;

				public Parser (string text)
				{
					lexer = new XkbLexer (text);
				}

				public bool TryParse (out Dictionary<uint, Key> keys, out string diagnostic)
				{
					keys = new Dictionary<uint, Key> ();
					diagnostic = null;

					try {
						while (!lexer.PeekIs (TokenKind.End)) {
							if (lexer.ReadIfIdentifier ("xkb_keycodes")) {
								ParseKeycodes ();
								continue;
							}

							if (lexer.ReadIfIdentifier ("xkb_types")) {
								ParseTypes ();
								continue;
							}

							if (lexer.ReadIfIdentifier ("xkb_compatibility") || lexer.ReadIfIdentifier ("xkb_compatibility_map") ||
								lexer.ReadIfIdentifier ("xkb_compat") || lexer.ReadIfIdentifier ("xkb_compat_map")) {
								ParseCompatibility ();
								continue;
							}

							if (lexer.ReadIfIdentifier ("xkb_symbols")) {
								ParseSymbols ();
								continue;
							}

							if (lexer.ReadIfIdentifier ("include")) {
								Unsupported ("include statement requires external XKB files");
								SkipToStatementEnd ();
								continue;
							}

							lexer.Read ();
						}
					} catch (InvalidOperationException e) {
						diagnostic = e.Message;
						return false;
					}

					if (unsupportedDiagnostic != null) {
						diagnostic = unsupportedDiagnostic;
						return false;
					}

					if (!BuildKeys (out keys, out diagnostic))
						return false;

					if (keys.Count == 0) {
						diagnostic = "managed XKB parser found no key symbols";
						return false;
					}

					return true;
				}

				bool BuildKeys (out Dictionary<uint, Key> keys, out string diagnostic)
				{
					keys = new Dictionary<uint, Key> ();
					diagnostic = null;
					Dictionary<string, uint> virtualModifierMasks = ResolveVirtualModifierMasks ();
					Dictionary<string, XkbResolvedType> resolvedTypes = new Dictionary<string, XkbResolvedType> (StringComparer.Ordinal);

					foreach (KeyValuePair<string, ParsedKey> item in symbols) {
						uint keycode;
						if (!keycodes.TryGetValue (item.Key, out keycode))
							continue;

						ParsedKey parsed = item.Value;
						KeyGroup [] groups = new KeyGroup [parsed.Groups.Length];
						for (int group = 0; group < parsed.Groups.Length; group++) {
							XkbSymbol [] groupSymbols = parsed.Groups [group];
							if (groupSymbols == null)
								continue;

							string typeName;
							if (!TryGetGroupTypeName (item.Key, parsed, group, groupSymbols, out typeName, out diagnostic))
								return false;

							XkbResolvedType type;
							if (!ResolveType (typeName, virtualModifierMasks, resolvedTypes, out type, out diagnostic))
								return false;

							KeyGroup keyGroup = new KeyGroup ();
							keyGroup.Symbols = groupSymbols;
							keyGroup.Type = type;
							groups [group] = keyGroup;
						}

						Key key = new Key ();
						key.Name = item.Key;
						key.Groups = groups;
						keys [keycode] = key;
					}

					return true;
				}

				bool TryGetGroupTypeName (string keyName, ParsedKey key, int group, XkbSymbol [] groupSymbols, out string typeName, out string diagnostic)
				{
					diagnostic = null;
					typeName = null;
					if (key.TypeNames != null && group < key.TypeNames.Length)
						typeName = key.TypeNames [group];
					if (!String.IsNullOrEmpty (typeName))
						return true;

					// XKB keymaps often omit per-key types.  The text format
					// defines the replacement: use key.type defaults first, then
					// infer one of the standard key types from the symbols.
					typeName = InferTypeName (groupSymbols);
					return true;
				}

				bool ResolveType (string name, Dictionary<string, uint> virtualModifierMasks, Dictionary<string, XkbResolvedType> resolvedTypes, out XkbResolvedType resolved, out string diagnostic)
				{
					diagnostic = null;
					if (resolvedTypes.TryGetValue (name, out resolved))
						return true;

					XkbType type;
					if (!types.TryGetValue (name, out type)) {
						type = CreateStandardType (name);
						if (type == null) {
							diagnostic = "referenced XKB type \"" + name + "\" was not found";
							return false;
						}
					}

					if (type.Name == "ONE_LEVEL" && type.ModifierNames.Length == 0 && type.Maps.Count == 0 && type.Preserves.Count == 0) {
						resolved = new XkbResolvedType ();
						resolved.Name = name;
						resolvedTypes [name] = resolved;
						return true;
					}

					uint modifierMask;
					if (!ResolveModifierNames (type.ModifierNames, virtualModifierMasks, out modifierMask, out diagnostic)) {
						diagnostic = "type \"" + name + "\": " + diagnostic;
						resolved = null;
						return false;
					}

					resolved = new XkbResolvedType ();
					resolved.Name = name;
					resolved.ModifierMask = modifierMask;

					foreach (XkbTypeMap map in type.Maps) {
						uint selectorMask;
						if (!ResolveModifierNames (map.ModifierNames, virtualModifierMasks, out selectorMask, out diagnostic)) {
							diagnostic = "type \"" + name + "\" map: " + diagnostic;
							resolved = null;
							return false;
						}
						if ((selectorMask & ~modifierMask) != 0) {
							diagnostic = "type \"" + name + "\" map references modifiers outside its modifier set";
							resolved = null;
							return false;
						}
						resolved.Levels [selectorMask] = map.Level;
					}

					foreach (XkbTypePreserve preserve in type.Preserves) {
						uint selectorMask;
						uint preserveMask;
						if (!ResolveModifierNames (preserve.ModifierNames, virtualModifierMasks, out selectorMask, out diagnostic)) {
							diagnostic = "type \"" + name + "\" preserve selector: " + diagnostic;
							resolved = null;
							return false;
						}
						if (!ResolveModifierNames (preserve.PreserveNames, virtualModifierMasks, out preserveMask, out diagnostic)) {
							diagnostic = "type \"" + name + "\" preserve value: " + diagnostic;
							resolved = null;
							return false;
						}
						resolved.Preserves [selectorMask & modifierMask] = preserveMask & modifierMask;
					}

					resolvedTypes [name] = resolved;
					return true;
				}

				Dictionary<string, uint> ResolveVirtualModifierMasks ()
				{
					// XKB virtual modifier encoding is explicit declarations OR
					// implicit bindings from real modifier maps whose keys carry
					// the virtual modifier through key properties or compat
					// interprets.
					Dictionary<string, uint> masks = new Dictionary<string, uint> (explicitVirtualModifierMasks, StringComparer.Ordinal);
					foreach (ModifierMapEntry map in modifierMaps) {
						foreach (ModifierMapMember member in map.Members) {
							if (member.IsKeyName) {
								ParsedKey key;
								if (symbols.TryGetValue (member.Name, out key))
									AddVirtualModifiersFromKey (masks, key, map.Mask);
							} else
								AddVirtualModifiersFromModifierMapSymbol (masks, member.Name, map.Mask);
						}
					}
					return masks;
				}

				void AddVirtualModifiersFromModifierMapSymbol (Dictionary<string, uint> masks, string symbolName, uint mask)
				{
					bool found = false;
					ParsedKey bestKey = null;
					uint bestKeycode = UInt32.MaxValue;
					int bestGroup = Int32.MaxValue;
					int bestLevel = Int32.MaxValue;

					foreach (KeyValuePair<string, ParsedKey> item in symbols) {
						uint keycode;
						if (!keycodes.TryGetValue (item.Key, out keycode))
							continue;
						ParsedKey key = item.Value;
						if (key.Groups == null)
							continue;
						for (int group = 0; group < key.Groups.Length; group++) {
							XkbSymbol [] groupSymbols = key.Groups [group];
							if (groupSymbols == null)
								continue;
							for (int level = 0; level < groupSymbols.Length; level++) {
								if (groupSymbols [level].Name != symbolName)
									continue;
								if (!found || group < bestGroup || group == bestGroup && (level < bestLevel || level == bestLevel && keycode < bestKeycode)) {
									found = true;
									bestKey = key;
									bestGroup = group;
									bestLevel = level;
									bestKeycode = keycode;
								}
							}
						}
					}

					if (found)
						AddVirtualModifiersFromKey (masks, bestKey, mask);
				}

				void AddVirtualModifiersFromKey (Dictionary<string, uint> masks, ParsedKey key, uint mask)
				{
					if (key.Groups == null)
						return;
					for (int i = 0; i < key.VirtualModifierNames.Length; i++)
						AddVirtualModifierMask (masks, key.VirtualModifierNames [i], mask);
					for (int group = 0; group < key.Groups.Length; group++) {
						XkbSymbol [] symbols = key.Groups [group];
						if (symbols == null)
							continue;
						for (int level = 0; level < symbols.Length; level++) {
							if (!symbols [level].NoSymbol)
								AddVirtualModifierFromSymbolName (masks, symbols [level].Name, group, level, mask);
						}
					}
				}

				void AddVirtualModifierFromSymbolName (Dictionary<string, uint> masks, string symbolName, int group, int level, uint keyModifierMask)
				{
					if (String.IsNullOrEmpty (symbolName))
						return;

					bool found = false;
					CompatInterpret best = new CompatInterpret ();
					int bestSymbolSpecificity = -1;
					int bestSpecificity = -1;
					foreach (CompatInterpret interpret in compatInterprets) {
						if (!InterpretSymbolMatches (interpret.SymbolName, symbolName))
							continue;
						if (interpret.LevelOneOnly && (group != 0 || level != 0))
							continue;
						if (!InterpretPredicateMatches (interpret, keyModifierMask))
							continue;

						int symbolSpecificity = InterpretSymbolSpecificity (interpret.SymbolName, symbolName);
						int specificity = InterpretPredicateSpecificity (interpret.PredicateOperation);
						if (!found || symbolSpecificity > bestSymbolSpecificity ||
							symbolSpecificity == bestSymbolSpecificity && (specificity > bestSpecificity ||
							specificity == bestSpecificity && interpret.Order < best.Order)) {
							found = true;
							best = interpret;
							bestSymbolSpecificity = symbolSpecificity;
							bestSpecificity = specificity;
						}
					}

					if (found)
						AddVirtualModifierMask (masks, best.VirtualModifierName, keyModifierMask);
				}

				static bool InterpretSymbolMatches (string pattern, string symbolName)
				{
					return pattern == symbolName || pattern == "Any" || pattern == "NoSymbol";
				}

				static int InterpretSymbolSpecificity (string pattern, string symbolName)
				{
					return pattern == symbolName ? 1 : 0;
				}

				static bool InterpretPredicateMatches (CompatInterpret interpret, uint keyModifierMask)
				{
					uint mask = interpret.PredicateMask;
					switch (interpret.PredicateOperation) {
					case InterpretPredicateOperation.AnyOfOrNone:
						return keyModifierMask == 0 || (keyModifierMask & mask) != 0;
					case InterpretPredicateOperation.AnyOf:
					case InterpretPredicateOperation.Any:
						return (keyModifierMask & mask) != 0;
					case InterpretPredicateOperation.NoneOf:
						return (keyModifierMask & mask) == 0;
					case InterpretPredicateOperation.AllOf:
						return (keyModifierMask & mask) == mask;
					case InterpretPredicateOperation.Exactly:
						return keyModifierMask == mask;
					default:
						return false;
					}
				}

				static int InterpretPredicateSpecificity (InterpretPredicateOperation operation)
				{
					switch (operation) {
					case InterpretPredicateOperation.AnyOfOrNone:
						return 0;
					case InterpretPredicateOperation.AnyOf:
						return 1;
					case InterpretPredicateOperation.Any:
						return 2;
					case InterpretPredicateOperation.NoneOf:
						return 3;
					case InterpretPredicateOperation.AllOf:
						return 4;
					case InterpretPredicateOperation.Exactly:
						return 5;
					default:
						return -1;
					}
				}

				static void AddVirtualModifierMask (Dictionary<string, uint> masks, string name, uint mask)
				{
					uint oldMask;
					if (masks.TryGetValue (name, out oldMask))
						masks [name] = oldMask | mask;
					else
						masks [name] = mask;
				}

				static bool ResolveModifierNames (string [] names, Dictionary<string, uint> virtualModifierMasks, out uint mask, out string diagnostic)
				{
					mask = 0;
					diagnostic = null;
					if (names == null)
						return true;

					for (int i = 0; i < names.Length; i++) {
						string name = names [i];
						if (String.IsNullOrEmpty (name) || IsNoModifierName (name))
							continue;

						if (IsUnsupportedModifierWildcard (name)) {
							diagnostic = "unsupported modifier wildcard " + name;
							return false;
						}

						uint namedMask = ModifierMaskForName (name);
						if (namedMask == 0 && !virtualModifierMasks.TryGetValue (name, out namedMask)) {
							diagnostic = "unresolved modifier " + name;
							return false;
						}
						mask |= namedMask;
					}

					return true;
				}

				void Unsupported (string diagnostic)
				{
					if (unsupportedDiagnostic == null)
						unsupportedDiagnostic = diagnostic;
				}

				void ParseKeycodes ()
				{
					if (!SkipToBlockStart ())
						throw new InvalidOperationException ("xkb_keycodes has no block");

					int depth = 1;
					while (depth > 0) {
						Token token = lexer.Read ();
						if (token.Kind == TokenKind.End)
							throw new InvalidOperationException ("unterminated xkb_keycodes block");

						if (token.Kind == TokenKind.Symbol) {
							if (token.Symbol == '{')
								depth++;
							else if (token.Symbol == '}')
								depth--;
							continue;
						}

						if (depth != 1)
							continue;

						if (token.Kind == TokenKind.Identifier && !ApplyMergeModePrefix (ref token))
							continue;

						if (token.Kind == TokenKind.KeyName) {
							string keyName = token.Text;
							if (!lexer.ReadIfSymbol ('='))
								continue;
							Token number = lexer.Read ();
							if (number.Kind == TokenKind.Number)
								keycodes [keyName] = number.Number;
							SkipToStatementEnd ();
							continue;
						}

						if (token.Kind == TokenKind.Identifier && token.Text == "alias") {
							Token alias = lexer.Read ();
							if (alias.Kind == TokenKind.KeyName && lexer.ReadIfSymbol ('=')) {
								Token target = lexer.Read ();
								uint targetCode;
								if (target.Kind == TokenKind.KeyName && keycodes.TryGetValue (target.Text, out targetCode))
									keycodes [alias.Text] = targetCode;
							}
							SkipToStatementEnd ();
						}
					}
				}

				void ParseTypes ()
				{
					if (!SkipToBlockStart ())
						throw new InvalidOperationException ("xkb_types has no block");

					int depth = 1;
					while (depth > 0) {
						Token token = lexer.Read ();
						if (token.Kind == TokenKind.End)
							throw new InvalidOperationException ("unterminated xkb_types block");

						if (token.Kind == TokenKind.Symbol) {
							if (token.Symbol == '{')
								depth++;
							else if (token.Symbol == '}')
								depth--;
							continue;
						}

						if (depth != 1 || token.Kind != TokenKind.Identifier)
							continue;

						if (!ApplyMergeModePrefix (ref token))
							continue;

						if (token.Text == "virtual_modifiers")
							ParseVirtualModifiers ();
						else if (token.Text == "type")
							ParseTypeStatement ();
						else
							SkipPropertyTail ();
					}
				}

				void ParseTypeStatement ()
				{
					Token nameToken = lexer.Read ();
					if (nameToken.Kind != TokenKind.String && nameToken.Kind != TokenKind.Identifier) {
						SkipStatementOrBlock ();
						return;
					}

					if (!SkipToBlockStart ()) {
						SkipToStatementEnd ();
						return;
					}

					XkbType type = new XkbType ();
					type.Name = TokenText (nameToken);
					int depth = 1;
					while (depth > 0) {
						Token token = lexer.Read ();
						if (token.Kind == TokenKind.End)
							throw new InvalidOperationException ("unterminated type \"" + type.Name + "\" block");

						if (token.Kind == TokenKind.Symbol) {
							if (token.Symbol == '{')
								depth++;
							else if (token.Symbol == '}')
								depth--;
							continue;
						}

						if (depth != 1 || token.Kind != TokenKind.Identifier)
							continue;

						if (token.Text == "modifiers") {
							if (lexer.ReadIfSymbol ('='))
								type.ModifierNames = ParseModifierNameListUntil (';');
							else
								SkipToStatementEnd ();
						} else if (token.Text == "map") {
							ParseTypeMap (type);
						} else if (token.Text == "preserve") {
							ParseTypePreserve (type);
						} else
							SkipPropertyTail ();
					}

					SkipToStatementEnd ();
					types [type.Name] = type;
				}

				void ParseTypeMap (XkbType type)
				{
					if (!lexer.ReadIfSymbol ('[')) {
						SkipToStatementEnd ();
						return;
					}

					string [] names = ParseModifierNameListUntil (']');
					if (!lexer.ReadIfSymbol ('=')) {
						SkipToStatementEnd ();
						return;
					}

					Token levelToken = lexer.Read ();
					int level;
					if (TryParseLevel (levelToken, out level)) {
						XkbTypeMap map = new XkbTypeMap ();
						map.ModifierNames = names;
						map.Level = level;
						type.Maps.Add (map);
					}
					SkipToStatementEnd ();
				}

				void ParseTypePreserve (XkbType type)
				{
					if (!lexer.ReadIfSymbol ('[')) {
						SkipToStatementEnd ();
						return;
					}

					string [] selectorNames = ParseModifierNameListUntil (']');
					if (!lexer.ReadIfSymbol ('=')) {
						SkipToStatementEnd ();
						return;
					}

					XkbTypePreserve preserve = new XkbTypePreserve ();
					preserve.ModifierNames = selectorNames;
					preserve.PreserveNames = ParseModifierNameListUntil (';');
					type.Preserves.Add (preserve);
				}

				void ParseCompatibility ()
				{
					if (!SkipToBlockStart ())
						throw new InvalidOperationException ("xkb_compatibility has no block");

					int depth = 1;
					while (depth > 0) {
						Token token = lexer.Read ();
						if (token.Kind == TokenKind.End)
							throw new InvalidOperationException ("unterminated xkb_compatibility block");

						if (token.Kind == TokenKind.Symbol) {
							if (token.Symbol == '{')
								depth++;
							else if (token.Symbol == '}')
								depth--;
							continue;
						}

						if (depth != 1 || token.Kind != TokenKind.Identifier)
							continue;

						if (!ApplyMergeModePrefix (ref token))
							continue;

						if (token.Text == "virtual_modifiers")
							ParseVirtualModifiers ();
						else if (token.Text == "include") {
							Unsupported ("include statement requires external XKB files");
							SkipToStatementEnd ();
						} else if (token.Text == "interpret" && !lexer.PeekIsSymbol ('.'))
							ParseInterpret ();
						else
							SkipPropertyTail ();
					}
				}

				void ParseInterpret ()
				{
					Token symbolToken = lexer.Read ();
					string symbolName = TokenText (symbolToken);
					if (String.IsNullOrEmpty (symbolName)) {
						SkipStatementOrBlock ();
						return;
					}
					bool levelOneOnly = false;
					string virtualModifierName = null;
					InterpretPredicateOperation predicateOperation = InterpretPredicateOperation.AnyOfOrNone;
					uint predicateMask = XkbAllRealModifierMask;
					if (lexer.ReadIfSymbol ('+')) {
						string diagnostic;
						if (!ParseInterpretPredicate (out predicateOperation, out predicateMask, out diagnostic))
							Unsupported (diagnostic);
					}

					if (!SkipToBlockStart ()) {
						SkipToStatementEnd ();
						return;
					}

					int depth = 1;
					while (depth > 0) {
						Token token = lexer.Read ();
						if (token.Kind == TokenKind.End)
							throw new InvalidOperationException ("unterminated interpret " + symbolName + " block");

						if (token.Kind == TokenKind.Symbol) {
							if (token.Symbol == '{')
								depth++;
							else if (token.Symbol == '}')
								depth--;
							continue;
						}

						if (depth != 1 || token.Kind != TokenKind.Identifier)
							continue;

						if (token.Text == "virtualModifier") {
							if (lexer.ReadIfSymbol ('=')) {
								Token value = lexer.Read ();
								if (value.Kind == TokenKind.Identifier)
									virtualModifierName = value.Text;
							}
							SkipToStatementEnd ();
						} else if (token.Text == "useModMapMods" || token.Text == "useModMap") {
							if (lexer.ReadIfSymbol ('=')) {
								Token value = lexer.Read ();
								string text = TokenText (value);
								levelOneOnly = String.Equals (text, "level1", StringComparison.OrdinalIgnoreCase) ||
									String.Equals (text, "Level1", StringComparison.Ordinal);
							}
							SkipToStatementEnd ();
						} else
							SkipPropertyTail ();
					}

					SkipToStatementEnd ();
					if (!String.IsNullOrEmpty (virtualModifierName)) {
						CompatInterpret interpret = new CompatInterpret ();
						interpret.SymbolName = symbolName;
						interpret.VirtualModifierName = virtualModifierName;
						interpret.LevelOneOnly = levelOneOnly;
						interpret.PredicateOperation = predicateOperation;
						interpret.PredicateMask = predicateMask;
						interpret.Order = nextInterpretOrder++;
						compatInterprets.Add (interpret);
					}
				}

				bool ParseInterpretPredicate (out InterpretPredicateOperation operation, out uint mask, out string diagnostic)
				{
					operation = InterpretPredicateOperation.Exactly;
					mask = 0;
					diagnostic = null;

					Token first = lexer.Read ();
					if (first.Kind == TokenKind.End) {
						diagnostic = "unterminated XKB interpret predicate";
						return false;
					}

					string text = TokenText (first);
					if (TryParseInterpretPredicateOperation (text, out operation)) {
						mask = XkbAllRealModifierMask;
						if (lexer.ReadIfSymbol ('('))
							return ParseRealModifierMaskUntil (')', out mask, out diagnostic);
						return true;
					}

					if (!AddRealModifierMaskToken (first, ref mask, out diagnostic))
						return false;

					while (true) {
						Token token = lexer.Peek ();
						if (token.Kind == TokenKind.End) {
							diagnostic = "unterminated XKB interpret predicate";
							return false;
						}
						if (token.Kind == TokenKind.Symbol && token.Symbol == '{')
							return true;

						token = lexer.Read ();
						if (token.Kind == TokenKind.Symbol)
							continue;
						if (!AddRealModifierMaskToken (token, ref mask, out diagnostic))
							return false;
					}
				}

				bool ParseRealModifierMaskUntil (char terminator, out uint mask, out string diagnostic)
				{
					mask = 0;
					diagnostic = null;
					while (true) {
						Token token = lexer.Read ();
						if (token.Kind == TokenKind.End) {
							diagnostic = "unterminated XKB real modifier mask";
							return false;
						}
						if (token.Kind == TokenKind.Symbol && token.Symbol == terminator)
							return true;
						if (token.Kind == TokenKind.Symbol)
							continue;
						if (!AddRealModifierMaskToken (token, ref mask, out diagnostic))
							return false;
					}
				}

				static bool TryParseInterpretPredicateOperation (string text, out InterpretPredicateOperation operation)
				{
					if (String.Equals (text, "AnyOfOrNone", StringComparison.OrdinalIgnoreCase)) {
						operation = InterpretPredicateOperation.AnyOfOrNone;
						return true;
					}
					if (String.Equals (text, "AnyOf", StringComparison.OrdinalIgnoreCase)) {
						operation = InterpretPredicateOperation.AnyOf;
						return true;
					}
					if (String.Equals (text, "Any", StringComparison.OrdinalIgnoreCase)) {
						operation = InterpretPredicateOperation.Any;
						return true;
					}
					if (String.Equals (text, "NoneOf", StringComparison.OrdinalIgnoreCase)) {
						operation = InterpretPredicateOperation.NoneOf;
						return true;
					}
					if (String.Equals (text, "AllOf", StringComparison.OrdinalIgnoreCase)) {
						operation = InterpretPredicateOperation.AllOf;
						return true;
					}
					if (String.Equals (text, "Exactly", StringComparison.OrdinalIgnoreCase)) {
						operation = InterpretPredicateOperation.Exactly;
						return true;
					}

					operation = InterpretPredicateOperation.Exactly;
					return false;
				}

				static bool AddRealModifierMaskToken (Token token, ref uint mask, out string diagnostic)
				{
					diagnostic = null;
					if (token.Kind == TokenKind.Identifier) {
						string name = token.Text;
						if (IsNoModifierName (name))
							return true;
						if (String.Equals (name, "all", StringComparison.OrdinalIgnoreCase) ||
							String.Equals (name, "any", StringComparison.OrdinalIgnoreCase)) {
							mask |= XkbAllRealModifierMask;
							return true;
						}
						uint namedMask = ModifierMaskForName (name);
						if (namedMask != 0) {
							mask |= namedMask;
							return true;
						}
						diagnostic = "XKB interpret predicate references non-real modifier " + name;
						return false;
					}

					if (token.Kind == TokenKind.Number) {
						if ((token.Number & ~XkbAllRealModifierMask) != 0) {
							diagnostic = "XKB interpret predicate numeric mask includes non-real modifiers";
							return false;
						}
						mask |= token.Number;
						return true;
					}

					diagnostic = "invalid XKB interpret predicate token";
					return false;
				}

				void ParseSymbols ()
				{
					if (!SkipToBlockStart ())
						throw new InvalidOperationException ("xkb_symbols has no block");

					int depth = 1;
					string [] savedDefaultTypeNames = (string []) defaultSymbolTypeNames.Clone ();
					while (depth > 0) {
						Token token = lexer.Read ();
						if (token.Kind == TokenKind.End)
							throw new InvalidOperationException ("unterminated xkb_symbols block");

						if (token.Kind == TokenKind.Symbol) {
							if (token.Symbol == '{')
								depth++;
							else if (token.Symbol == '}')
								depth--;
							continue;
						}

						if (depth != 1 || token.Kind != TokenKind.Identifier)
							continue;

						if (!ApplyMergeModePrefix (ref token))
							continue;

						if (token.Text == "virtual_modifiers")
							ParseVirtualModifiers ();
						else if (token.Text == "include") {
							Unsupported ("include statement requires external XKB files");
							SkipToStatementEnd ();
						} else if (token.Text == "key") {
							if (lexer.PeekIsSymbol ('.'))
								ParseKeyDefaultStatement ();
							else
								ParseKeyStatement ();
						}
						else if (token.Text == "type")
							ParseDefaultTypeStatement ();
						else if (token.Text == "modifier_map" || token.Text == "mod_map" || token.Text == "modmap")
							ParseModifierMap ();
						else
							SkipPropertyTail ();
					}
					Array.Copy (savedDefaultTypeNames, defaultSymbolTypeNames, defaultSymbolTypeNames.Length);
				}

				void ParseVirtualModifiers ()
				{
					while (true) {
						Token name = lexer.Read ();
						if (name.Kind == TokenKind.End)
							throw new InvalidOperationException ("unterminated virtual_modifiers statement");
						if (name.Kind == TokenKind.Symbol && name.Symbol == ';')
							break;
						if (name.Kind != TokenKind.Identifier) {
							SkipToStatementEnd ();
							break;
						}

						uint mask = 0;
						if (lexer.ReadIfSymbol ('=')) {
							string diagnostic;
							if (!ParseModifierMaskUntil (new char [] { ',', ';' }, out mask, out diagnostic))
								Unsupported (diagnostic);
						}
						if (!explicitVirtualModifierMasks.ContainsKey (name.Text))
							explicitVirtualModifierMasks [name.Text] = mask;

						Token separator = lexer.Peek ();
						if (separator.Kind == TokenKind.Symbol && separator.Symbol == ',') {
							lexer.Read ();
							continue;
						}
						if (separator.Kind == TokenKind.Symbol && separator.Symbol == ';') {
							lexer.Read ();
							break;
						}
					}
				}

				void ParseKeyDefaultStatement ()
				{
					if (!lexer.ReadIfSymbol ('.')) {
						SkipToStatementEnd ();
						return;
					}

					Token property = lexer.Read ();
					if (property.Kind != TokenKind.Identifier || property.Text != "type") {
						SkipToStatementEnd ();
						return;
					}

					ParseDefaultTypeStatement ();
				}

				void ParseDefaultTypeStatement ()
				{
					int group = 0;
					if (lexer.ReadIfSymbol ('['))
						group = ParseGroupSelector ();
					if (!lexer.ReadIfSymbol ('=')) {
						SkipToStatementEnd ();
						return;
					}

					Token typeToken = lexer.Read ();
					if (group >= 0 && group < defaultSymbolTypeNames.Length && (typeToken.Kind == TokenKind.String || typeToken.Kind == TokenKind.Identifier))
						defaultSymbolTypeNames [group] = TokenText (typeToken);
					SkipToStatementEnd ();
				}

				void ParseKeyStatement ()
				{
					Token nameToken = lexer.Read ();
					if (nameToken.Kind != TokenKind.KeyName) {
						SkipStatementOrBlock ();
						return;
					}

					if (!SkipToBlockStart ()) {
						SkipToStatementEnd ();
						return;
					}

					Dictionary<int, XkbSymbol []> groups = new Dictionary<int, XkbSymbol []> ();
					Dictionary<int, string> typeNames = new Dictionary<int, string> ();
					List<string> virtualModifierNames = new List<string> ();
					int nextGroup = 0;
					int depth = 1;
					while (depth > 0) {
						Token token = lexer.Read ();
						if (token.Kind == TokenKind.End)
							throw new InvalidOperationException ("unterminated key <" + nameToken.Text + "> block");

						if (token.Kind == TokenKind.Symbol) {
							if (token.Symbol == '{')
								depth++;
							else if (token.Symbol == '}')
								depth--;
							else if (depth == 1 && token.Symbol == '[')
								groups [nextGroup++] = ParseSymbolArray ();
							continue;
						}

						if (depth != 1 || token.Kind != TokenKind.Identifier) {
							if (depth == 1)
								SkipToStatementEnd ();
							continue;
						}

						if (token.Text == "symbols") {
							int group = -1;
							if (lexer.ReadIfSymbol ('['))
								group = ParseGroupSelector ();
							if (!lexer.ReadIfSymbol ('=') || !lexer.ReadIfSymbol ('[')) {
								SkipKeyFieldEnd ();
								continue;
							}
							if (group < 0)
								group = nextGroup++;
							groups [group] = ParseSymbolArray ();
						} else if (token.Text == "type") {
							int group = 0;
							if (lexer.ReadIfSymbol ('['))
								group = ParseGroupSelector ();
							if (!lexer.ReadIfSymbol ('=')) {
								SkipKeyFieldEnd ();
								continue;
							}
							Token typeToken = lexer.Read ();
							if (typeToken.Kind == TokenKind.String || typeToken.Kind == TokenKind.Identifier)
								typeNames [group] = TokenText (typeToken);
							SkipKeyFieldEnd ();
						} else if (token.Text == "virtualModifiers" || token.Text == "virtualmodifiers" || token.Text == "virtualModifier") {
							if (lexer.ReadIfSymbol ('=')) {
								string [] names = ParseModifierNameListUntilKeyField ();
								virtualModifierNames.AddRange (names);
							} else
								SkipKeyFieldEnd ();
						} else
							SkipKeyPropertyTail ();
					}

					SkipToStatementEnd ();
					if (groups.Count == 0)
						return;

					int maxGroup = -1;
					foreach (int group in groups.Keys) {
						if (group > maxGroup)
							maxGroup = group;
					}
					foreach (int group in typeNames.Keys) {
						if (group > maxGroup)
							maxGroup = group;
					}

					ParsedKey key = new ParsedKey ();
					key.Groups = new XkbSymbol [maxGroup + 1][];
					key.TypeNames = new string [maxGroup + 1];
					foreach (KeyValuePair<int, XkbSymbol []> group in groups)
						key.Groups [group.Key] = group.Value;
					for (int group = 0; group < key.TypeNames.Length && group < defaultSymbolTypeNames.Length; group++)
						key.TypeNames [group] = defaultSymbolTypeNames [group];
					foreach (KeyValuePair<int, string> typeName in typeNames)
						key.TypeNames [typeName.Key] = typeName.Value;
					key.VirtualModifierNames = virtualModifierNames.ToArray ();
					symbols [nameToken.Text] = key;
				}

				void ParseModifierMap ()
				{
					Token modifier = lexer.Read ();
					string modifierName = TokenText (modifier);
					bool deleteMap = IsNoModifierName (modifierName);
					uint modifierMask = deleteMap ? 0 : ModifierMaskForName (modifierName);
					if (!deleteMap && modifierMask == 0)
						Unsupported ("modifier_map uses non-real modifier " + modifierName);
					if (!SkipToBlockStart ()) {
						SkipToStatementEnd ();
						return;
					}

					ModifierMapEntry entry = new ModifierMapEntry ();
					entry.Mask = modifierMask;
					int depth = 1;
					while (depth > 0) {
						Token token = lexer.Read ();
						if (token.Kind == TokenKind.End)
							throw new InvalidOperationException ("unterminated modifier_map block");

						if (token.Kind == TokenKind.Symbol) {
							if (token.Symbol == '{')
								depth++;
							else if (token.Symbol == '}')
								depth--;
							continue;
						}

						if (depth == 1 && (token.Kind == TokenKind.KeyName || token.Kind == TokenKind.Identifier)) {
							ModifierMapMember member = new ModifierMapMember ();
							member.IsKeyName = token.Kind == TokenKind.KeyName;
							member.Name = TokenText (token);
							entry.Members.Add (member);
						}
					}

					SkipToStatementEnd ();
					if (deleteMap) {
						for (int i = 0; i < entry.Members.Count; i++)
							RemoveModifierMapMember (entry.Members [i]);
					} else if (entry.Mask != 0 && entry.Members.Count > 0)
						AddModifierMapEntry (entry);
				}

				void AddModifierMapEntry (ModifierMapEntry entry)
				{
					for (int i = 0; i < entry.Members.Count; i++)
						RemoveModifierMapMember (entry.Members [i]);
					modifierMaps.Add (entry);
				}

				void RemoveModifierMapMember (ModifierMapMember member)
				{
					for (int i = modifierMaps.Count - 1; i >= 0; i--) {
						List<ModifierMapMember> members = modifierMaps [i].Members;
						for (int j = members.Count - 1; j >= 0; j--) {
							if (ModifierMapMemberEquals (members [j], member))
								members.RemoveAt (j);
						}
						if (members.Count == 0)
							modifierMaps.RemoveAt (i);
					}
				}

				static bool ModifierMapMemberEquals (ModifierMapMember a, ModifierMapMember b)
				{
					return a.IsKeyName == b.IsKeyName && a.Name == b.Name;
				}

				XkbSymbol [] ParseSymbolArray ()
				{
					List<XkbSymbol> list = new List<XkbSymbol> ();
					int depth = 1;
					while (depth > 0) {
						Token token = lexer.Read ();
						if (token.Kind == TokenKind.End)
							throw new InvalidOperationException ("unterminated XKB symbols array");

						if (token.Kind == TokenKind.Symbol) {
							if (token.Symbol == '[')
								depth++;
							else if (token.Symbol == ']')
								depth--;
							else if (depth == 1 && token.Symbol == '{')
								list.Add (ParseSymbolSet ());
							continue;
						}

						if (depth == 1) {
							XkbSymbol symbol;
							if (TryCreateSymbol (token, out symbol))
								list.Add (symbol);
							else if (token.Kind == TokenKind.Identifier || token.Kind == TokenKind.Number || token.Kind == TokenKind.String) {
								Unsupported ("unknown XKB keysym " + TokenText (token));
								list.Add (NoSymbol ());
							}
						}
					}

					return list.ToArray ();
				}

				XkbSymbol ParseSymbolSet ()
				{
					List<XkbSymbol> symbols = new List<XkbSymbol> ();
					int depth = 1;
					while (depth > 0) {
						Token token = lexer.Read ();
						if (token.Kind == TokenKind.End)
							throw new InvalidOperationException ("unterminated XKB multi-symbol level");

						if (token.Kind == TokenKind.Symbol) {
							if (token.Symbol == '{') {
								Unsupported ("nested XKB multi-symbol level");
								depth++;
							} else if (token.Symbol == '}')
								depth--;
							continue;
						}

						if (depth == 1) {
							XkbSymbol symbol;
							if (TryCreateSymbol (token, out symbol))
								symbols.Add (symbol);
							else if (token.Kind == TokenKind.Identifier || token.Kind == TokenKind.Number || token.Kind == TokenKind.String)
								Unsupported ("unknown XKB keysym " + TokenText (token));
						}
					}

					if (symbols.Count == 0)
						return NoSymbol ();

					// XKB may provide several keysyms for one level.  WinForms
					// wants one virtual key plus text, so keep the first keysym
					// for KeyCode mapping and concatenate the printable text.
					StringBuilder text = new StringBuilder ();
					XkbSymbol result = NoSymbol ();
					for (int i = 0; i < symbols.Count; i++) {
						if (symbols [i].NoSymbol)
							continue;
						if (result.NoSymbol)
							result = symbols [i];
						if (!String.IsNullOrEmpty (symbols [i].Text))
							text.Append (symbols [i].Text);
					}
					if (result.NoSymbol)
						return NoSymbol ();
					if (text.Length > 0)
						result.Text = text.ToString ();
					return result;
				}

				string [] ParseModifierNameListUntil (char terminator)
				{
					List<string> names = new List<string> ();
					int depth = 0;
					while (true) {
						Token token = lexer.Read ();
						if (token.Kind == TokenKind.End)
							throw new InvalidOperationException ("unterminated XKB modifier expression");

						if (token.Kind == TokenKind.Symbol) {
							if (depth == 0 && token.Symbol == terminator)
								break;
							if (token.Symbol == '(' || token.Symbol == '[' || token.Symbol == '{')
								depth++;
							else if ((token.Symbol == ')' || token.Symbol == ']' || token.Symbol == '}') && depth > 0)
								depth--;
							continue;
						}

						if (depth == 0 && token.Kind == TokenKind.Identifier) {
							string name = token.Text;
							if (!IsNoModifierName (name))
								names.Add (name);
						} else if (depth == 0 && token.Kind == TokenKind.Number)
							Unsupported ("numeric XKB modifier mask requires libxkbcommon modifier indices");
					}

					return names.Count == 0 ? EmptyStringArray : names.ToArray ();
				}

				bool ParseModifierMaskUntil (char [] terminators, out uint mask, out string diagnostic)
				{
					mask = 0;
					diagnostic = null;
					int depth = 0;
					while (true) {
						Token token = lexer.Peek ();
						if (token.Kind == TokenKind.End) {
							diagnostic = "unterminated XKB virtual modifier mask";
							return false;
						}
						if (depth == 0 && token.Kind == TokenKind.Symbol && IsTerminator (token.Symbol, terminators))
							return true;

						token = lexer.Read ();

						if (token.Kind == TokenKind.Symbol) {
							if (token.Symbol == '(' || token.Symbol == '[' || token.Symbol == '{')
								depth++;
							else if ((token.Symbol == ')' || token.Symbol == ']' || token.Symbol == '}') && depth > 0)
								depth--;
							continue;
						}

						if (depth != 0)
							continue;

						if (token.Kind == TokenKind.Number) {
							mask |= token.Number;
							continue;
						}

						if (token.Kind == TokenKind.Identifier) {
							string name = token.Text;
							if (IsNoModifierName (name))
								continue;
							uint namedMask = ModifierMaskForName (name);
							if (namedMask == 0) {
								diagnostic = "virtual_modifiers explicit mask references non-real modifier " + name;
								return false;
							}
							mask |= namedMask;
						}
					}
				}

				string [] ParseModifierNameListUntilKeyField ()
				{
					List<string> names = new List<string> ();
					int depth = 0;
					while (true) {
						Token token = lexer.Peek ();
						if (token.Kind == TokenKind.End)
							return names.Count == 0 ? EmptyStringArray : names.ToArray ();
						if (depth == 0 && token.Kind == TokenKind.Symbol && token.Symbol == ',') {
							lexer.Read ();
							return names.Count == 0 ? EmptyStringArray : names.ToArray ();
						}
						if (depth == 0 && token.Kind == TokenKind.Symbol && token.Symbol == '}')
							return names.Count == 0 ? EmptyStringArray : names.ToArray ();

						token = lexer.Read ();
						if (token.Kind == TokenKind.Symbol) {
							if (token.Symbol == '(' || token.Symbol == '[' || token.Symbol == '{')
								depth++;
							else if ((token.Symbol == ')' || token.Symbol == ']' || token.Symbol == '}') && depth > 0)
								depth--;
							continue;
						}

						if (depth == 0 && token.Kind == TokenKind.Identifier && !IsNoModifierName (token.Text))
							names.Add (token.Text);
						else if (depth == 0 && token.Kind == TokenKind.Number)
							Unsupported ("numeric XKB virtual modifier map requires libxkbcommon modifier indices");
					}
				}

				bool ApplyMergeModePrefix (ref Token token)
				{
					if (token.Kind != TokenKind.Identifier)
						return true;
					if (token.Text == "override") {
						token = lexer.Read ();
						if (token.Kind == TokenKind.String) {
							Unsupported ("include statement requires external XKB files");
							SkipToStatementEnd ();
							return false;
						}
						return token.Kind != TokenKind.End;
					}
					if (IsUnsupportedMergeMode (token.Text)) {
						Unsupported ("unsupported XKB merge mode " + token.Text);
						SkipStatementOrBlock ();
						return false;
					}
					return true;
				}

				void SkipPropertyTail ()
				{
					if (lexer.ReadIfSymbol ('['))
						SkipBalanced (']');
					if (lexer.ReadIfSymbol ('='))
						SkipToStatementEnd ();
					else
						SkipStatementOrBlock ();
				}

				void SkipKeyPropertyTail ()
				{
					if (lexer.ReadIfSymbol ('['))
						SkipBalanced (']');
					if (lexer.ReadIfSymbol ('='))
						SkipKeyFieldEnd ();
					else
						SkipKeyFieldEnd ();
				}

				void SkipKeyFieldEnd ()
				{
					int depth = 0;
					while (true) {
						Token token = lexer.Peek ();
						if (token.Kind == TokenKind.End)
							return;
						if (depth == 0 && token.Kind == TokenKind.Symbol && token.Symbol == ',') {
							lexer.Read ();
							return;
						}
						if (depth == 0 && token.Kind == TokenKind.Symbol && token.Symbol == '}')
							return;
						token = lexer.Read ();
						if (token.Kind != TokenKind.Symbol)
							continue;
						if (token.Symbol == '{' || token.Symbol == '[' || token.Symbol == '(')
							depth++;
						else if ((token.Symbol == '}' || token.Symbol == ']' || token.Symbol == ')') && depth > 0)
							depth--;
					}
				}

				void SkipBalanced (char close)
				{
					char open = close == ']' ? '[' : close == ')' ? '(' : '{';
					int depth = 1;
					while (depth > 0) {
						Token token = lexer.Read ();
						if (token.Kind == TokenKind.End)
							throw new InvalidOperationException ("unterminated XKB property");
						if (token.Kind != TokenKind.Symbol)
							continue;
						if (token.Symbol == open)
							depth++;
						else if (token.Symbol == close)
							depth--;
					}
				}

				int ParseGroupSelector ()
				{
					int group = -1;
					while (true) {
						Token token = lexer.Read ();
						if (token.Kind == TokenKind.End)
							throw new InvalidOperationException ("unterminated XKB group selector");
						if (token.Kind == TokenKind.Symbol && token.Symbol == ']')
							break;

						if (token.Kind == TokenKind.Identifier) {
							string text = token.Text;
							if (text.StartsWith ("Group", StringComparison.Ordinal) || text.StartsWith ("group", StringComparison.Ordinal)) {
								int value;
								if (Int32.TryParse (text.Substring (5), out value) && value > 0)
									group = value - 1;
							}
						} else if (token.Kind == TokenKind.Number && token.Number > 0)
							group = checked ((int) token.Number - 1);
					}

					return group;
				}

				bool SkipToBlockStart ()
				{
					while (true) {
						Token token = lexer.Read ();
						if (token.Kind == TokenKind.End)
							return false;
						if (token.Kind == TokenKind.Symbol && token.Symbol == '{')
							return true;
						if (token.Kind == TokenKind.Symbol && token.Symbol == ';')
							return false;
					}
				}

				void SkipToStatementEnd ()
				{
					int depth = 0;
					while (true) {
						Token token = lexer.Peek ();
						if (token.Kind == TokenKind.End)
							return;
						if (depth == 0 && token.Kind == TokenKind.Symbol && token.Symbol == ';') {
							lexer.Read ();
							return;
						}
						token = lexer.Read ();
						if (token.Kind != TokenKind.Symbol)
							continue;
						if (token.Symbol == '{' || token.Symbol == '[' || token.Symbol == '(')
							depth++;
						else if ((token.Symbol == '}' || token.Symbol == ']' || token.Symbol == ')') && depth > 0)
							depth--;
					}
				}

				void SkipStatementOrBlock ()
				{
					while (true) {
						Token token = lexer.Read ();
						if (token.Kind == TokenKind.End)
							return;
						if (token.Kind == TokenKind.Symbol && token.Symbol == ';')
							return;
						if (token.Kind == TokenKind.Symbol && token.Symbol == '{') {
							int depth = 1;
							while (depth > 0) {
								token = lexer.Read ();
								if (token.Kind == TokenKind.End)
									return;
								if (token.Kind == TokenKind.Symbol && token.Symbol == '{')
									depth++;
								else if (token.Kind == TokenKind.Symbol && token.Symbol == '}')
									depth--;
							}
							SkipToStatementEnd ();
							return;
						}
					}
				}
			}

			sealed class XkbLexer {
				readonly string text;
				int position;
				Token? peeked;

				public XkbLexer (string text)
				{
					this.text = text ?? String.Empty;
				}

				public bool PeekIs (TokenKind kind)
				{
					return Peek ().Kind == kind;
				}

				public bool PeekIsSymbol (char value)
				{
					Token token = Peek ();
					return token.Kind == TokenKind.Symbol && token.Symbol == value;
				}

				public Token Peek ()
				{
					if (peeked == null)
						peeked = ReadToken ();
					return peeked.Value;
				}

				public Token Read ()
				{
					Token token = Peek ();
					peeked = null;
					return token;
				}

				public bool ReadIfIdentifier (string value)
				{
					Token token = Peek ();
					if (token.Kind != TokenKind.Identifier || token.Text != value)
						return false;
					Read ();
					return true;
				}

				public bool ReadIfSymbol (char value)
				{
					Token token = Peek ();
					if (token.Kind != TokenKind.Symbol || token.Symbol != value)
						return false;
					Read ();
					return true;
				}

				Token ReadToken ()
				{
					SkipWhitespaceAndComments ();
					if (position >= text.Length)
						return new Token { Kind = TokenKind.End };

					char c = text [position++];
					if (c == '"')
						return ReadString ();
					if (c == '<')
						return ReadKeyName ();
					if (Char.IsDigit (c))
						return ReadNumber (c);
					if (IsIdentifierStart (c))
						return ReadIdentifier (c);

					Token token = new Token ();
					token.Kind = TokenKind.Symbol;
					token.Symbol = c;
					return token;
				}

				Token ReadString ()
				{
					StringBuilder builder = new StringBuilder ();
					while (position < text.Length) {
						char c = text [position++];
						if (c == '"')
							break;
						if (c == '\\' && position < text.Length) {
							char escaped = text [position++];
							switch (escaped) {
							case 'b': builder.Append ('\b'); break;
							case 'f': builder.Append ('\f'); break;
							case 'n': builder.Append ('\n'); break;
							case 'r': builder.Append ('\r'); break;
							case 't': builder.Append ('\t'); break;
							case 'v': builder.Append ('\v'); break;
							case '"': builder.Append ('"'); break;
							case '\\': builder.Append ('\\'); break;
							case 'u':
								uint codepoint;
								if (ReadBraceCodepoint (out codepoint))
									builder.Append (Char.ConvertFromUtf32 ((int) codepoint));
								else
									builder.Append (escaped);
								break;
							default:
								if (escaped >= '0' && escaped <= '7')
									builder.Append (Char.ConvertFromUtf32 ((int) ReadOctalEscape (escaped)));
								else
									builder.Append (escaped);
								break;
							}
						} else
							builder.Append (c);
					}

					return new Token { Kind = TokenKind.String, Text = builder.ToString () };
				}

				uint ReadOctalEscape (char first)
				{
					uint value = (uint) (first - '0');
					int digits = 1;
					while (digits < 4 && position < text.Length && text [position] >= '0' && text [position] <= '7') {
						value = (value << 3) | (uint) (text [position] - '0');
						position++;
						digits++;
					}
					return value;
				}

				bool ReadBraceCodepoint (out uint codepoint)
				{
					codepoint = 0;
					if (position >= text.Length || text [position] != '{')
						return false;
					position++;
					int start = position;
					while (position < text.Length && text [position] != '}') {
						if (!IsHexDigit (text [position]))
							return false;
						position++;
					}
					if (position >= text.Length || position == start)
						return false;
					string hex = text.Substring (start, position - start);
					position++;
					uint value = 0;
					for (int i = 0; i < hex.Length; i++) {
						char c = hex [i];
						uint digit;
						if (c >= '0' && c <= '9')
							digit = (uint) (c - '0');
						else if (c >= 'a' && c <= 'f')
							digit = (uint) (c - 'a' + 10);
						else
							digit = (uint) (c - 'A' + 10);
						value = (value << 4) | digit;
					}
					if (value == 0 || value > UnicodeMaxCodePoint)
						return false;
					codepoint = value;
					return true;
				}

				Token ReadKeyName ()
				{
					int start = position;
					while (position < text.Length && text [position] != '>')
						position++;
					string keyName = text.Substring (start, position - start);
					if (position < text.Length && text [position] == '>')
						position++;
					return new Token { Kind = TokenKind.KeyName, Text = keyName };
				}

				Token ReadNumber (char first)
				{
					int start = position - 1;
					if (first == '0' && position < text.Length && (text [position] == 'x' || text [position] == 'X')) {
						position++;
						while (position < text.Length && IsHexDigit (text [position]))
							position++;
						return new Token {
							Kind = TokenKind.Number,
							Text = text.Substring (start, position - start),
							Number = Convert.ToUInt32 (text.Substring (start + 2, position - start - 2), 16)
						};
					}

					while (position < text.Length && Char.IsDigit (text [position]))
						position++;
					return new Token { Kind = TokenKind.Number, Text = text.Substring (start, position - start), Number = Convert.ToUInt32 (text.Substring (start, position - start), 10) };
				}

				Token ReadIdentifier (char first)
				{
					int start = position - 1;
					while (position < text.Length && IsIdentifierPart (text [position]))
						position++;
					return new Token { Kind = TokenKind.Identifier, Text = text.Substring (start, position - start) };
				}

				void SkipWhitespaceAndComments ()
				{
					while (position < text.Length) {
						char c = text [position];
						if (Char.IsWhiteSpace (c)) {
							position++;
							continue;
						}

						if (c == '#') {
							while (position < text.Length && text [position] != '\n')
								position++;
							continue;
						}

						if (c == '/' && position + 1 < text.Length && text [position + 1] == '/') {
							position += 2;
							while (position < text.Length && text [position] != '\n')
								position++;
							continue;
						}

						if (c == '/' && position + 1 < text.Length && text [position + 1] == '*') {
							position += 2;
							while (position + 1 < text.Length && !(text [position] == '*' && text [position + 1] == '/'))
								position++;
							if (position + 1 < text.Length)
								position += 2;
							continue;
						}

						break;
					}
				}

				static bool IsIdentifierStart (char c)
				{
					return Char.IsLetter (c) || c == '_';
				}

				static bool IsIdentifierPart (char c)
				{
					return Char.IsLetterOrDigit (c) || c == '_';
				}

				static bool IsHexDigit (char c)
				{
					return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
				}
			}

			struct XkbLevelSelection {
				public int Level;
				public uint ConsumedModifiers;
			}

			static readonly string [] EmptyStringArray = new string [0];
			readonly Dictionary<uint, Key> keys;
			uint activeModifiers;
			uint activeGroup;
			Keys modifierKeys;

			ManagedXkbKeyboardLayout (Dictionary<uint, Key> keys)
			{
				this.keys = keys;
			}

			public int KeyCount {
				get { return keys.Count; }
			}

			public static IWaylandKeyboardLayout TryCreate (WaylandKeymap keymap, out string diagnostic)
			{
				diagnostic = null;
				if (keymap.Format != WaylandProtocol.WlKeyboard.KeymapFormatXkbV1) {
					diagnostic = "unsupported Wayland keymap format " + keymap.Format.ToString ();
					return null;
				}
				if (String.IsNullOrEmpty (keymap.Text)) {
					diagnostic = "empty xkb_v1 keymap text";
					return null;
				}

				Dictionary<uint, Key> keys;
				Parser parser = new Parser (keymap.Text);
				if (!parser.TryParse (out keys, out diagnostic))
					return null;

				int printableKeys = CountPrintableKeys (keys);
				if (printableKeys == 0) {
					diagnostic = "managed XKB parser found no printable key symbols";
					return null;
				}

				diagnostic = "parsed " + keys.Count.ToString () + " keys, " + printableKeys.ToString () + " printable";
				return new ManagedXkbKeyboardLayout (keys);
			}

			public Keys ModifierKeys {
				get { return modifierKeys; }
			}

			public void SetModifiers (uint depressed, uint latched, uint locked, uint group)
			{
				activeModifiers = depressed | latched | locked;
				activeGroup = group;
				modifierKeys = KeysFromModifierMask (activeModifiers);
			}

			public WaylandKeyResult TranslateKey (uint evdevKey, bool pressed)
			{
				WaylandKeyResult result = new WaylandKeyResult ();
				result.ShortcutModifiers = modifierKeys;
				result.HasShortcutModifiers = true;

				Key key;
				if (!keys.TryGetValue (evdevKey + XkbKeycodeOffset, out key)) {
					result.KeyCode = MapEvdevKey (evdevKey);
					return result;
				}

				KeyGroup group = SelectGroup (key);
				if (group == null || group.Symbols == null || group.Symbols.Length == 0) {
					result.KeyCode = MapEvdevKey (evdevKey);
					return result;
				}

				XkbLevelSelection selection = SelectLevel (group.Type);
				result.ShortcutModifiers = KeysFromModifierMask (activeModifiers & ~selection.ConsumedModifiers);

				XkbSymbol symbol = selection.Level >= 0 && selection.Level < group.Symbols.Length ? group.Symbols [selection.Level] : NoSymbol ();
				if (!symbol.NoSymbol) {
					result.KeyCode = MapKeysymToKeys (symbol.Keysym);
					if (pressed && !String.IsNullOrEmpty (symbol.Text) && (result.ShortcutModifiers & Keys.Control) == 0)
						result.Text = symbol.Text;
				}
				if (result.KeyCode == Keys.None)
					result.KeyCode = MapEvdevKey (evdevKey);
				return result;
			}

			public void Dispose ()
			{
			}

			KeyGroup SelectGroup (Key key)
			{
				int group = activeGroup <= Int32.MaxValue ? (int) activeGroup : 0;
				if (key.Groups != null && group >= 0 && group < key.Groups.Length && key.Groups [group] != null)
					return key.Groups [group];
				if (key.Groups != null && key.Groups.Length > 0)
					return key.Groups [0];
				return null;
			}

			XkbLevelSelection SelectLevel (XkbResolvedType type)
			{
				XkbLevelSelection selection = new XkbLevelSelection ();
				uint selector = activeModifiers & type.ModifierMask;
				int level;
				if (type.Levels.TryGetValue (selector, out level))
					selection.Level = level;
				else
					selection.Level = 0;

				uint preserve;
				if (!type.Preserves.TryGetValue (selector, out preserve))
					preserve = 0;
				selection.ConsumedModifiers = type.ModifierMask & ~preserve;
				return selection;
			}

			static string TokenText (Token token)
			{
				if (token.Kind == TokenKind.Number)
					return token.Text ?? token.Number.ToString ();
				return token.Text;
			}

			static bool TryParseLevel (Token token, out int level)
			{
				level = 0;
				string text = TokenText (token);
				if (String.IsNullOrEmpty (text))
					return false;
				if (text.StartsWith ("Level", StringComparison.Ordinal)) {
					int value;
					if (Int32.TryParse (text.Substring (5), out value) && value > 0) {
						level = value - 1;
						return true;
					}
				}
				if (token.Kind == TokenKind.Number && token.Number > 0) {
					level = checked ((int) token.Number - 1);
					return true;
				}
				return false;
			}

			static bool IsTerminator (char value, char [] terminators)
			{
				for (int i = 0; i < terminators.Length; i++) {
					if (terminators [i] == value)
						return true;
				}
				return false;
			}

			static bool IsUnsupportedMergeMode (string text)
			{
				return text == "augment" || text == "replace" || text == "alternate";
			}

			static XkbType CreateStandardType (string name)
			{
				XkbType type = new XkbType ();
				type.Name = name;
				switch (name) {
				case "ONE_LEVEL":
					return type;
				case "TWO_LEVEL":
					type.ModifierNames = new string [] { "Shift" };
					AddTypeMap (type, EmptyStringArray, 0);
					AddTypeMap (type, new string [] { "Shift" }, 1);
					return type;
				case "ALPHABETIC":
					type.ModifierNames = new string [] { "Shift", "Lock" };
					AddTypeMap (type, EmptyStringArray, 0);
					AddTypeMap (type, new string [] { "Shift" }, 1);
					AddTypeMap (type, new string [] { "Lock" }, 1);
					AddTypeMap (type, new string [] { "Shift", "Lock" }, 0);
					return type;
				case "FOUR_LEVEL":
					type.ModifierNames = new string [] { "Shift", "LevelThree" };
					AddTypeMap (type, EmptyStringArray, 0);
					AddTypeMap (type, new string [] { "Shift" }, 1);
					AddTypeMap (type, new string [] { "LevelThree" }, 2);
					AddTypeMap (type, new string [] { "Shift", "LevelThree" }, 3);
					return type;
				case "FOUR_LEVEL_SEMIALPHABETIC":
					type.ModifierNames = new string [] { "Shift", "Lock", "LevelThree" };
					AddTypeMap (type, EmptyStringArray, 0);
					AddTypeMap (type, new string [] { "Shift" }, 1);
					AddTypeMap (type, new string [] { "Lock" }, 1);
					AddTypeMap (type, new string [] { "Shift", "Lock" }, 0);
					AddTypeMap (type, new string [] { "LevelThree" }, 2);
					AddTypeMap (type, new string [] { "Shift", "LevelThree" }, 3);
					AddTypeMap (type, new string [] { "Lock", "LevelThree" }, 2);
					AddTypeMap (type, new string [] { "Shift", "Lock", "LevelThree" }, 3);
					AddTypePreserve (type, new string [] { "Lock", "LevelThree" }, new string [] { "Lock" });
					AddTypePreserve (type, new string [] { "Shift", "Lock", "LevelThree" }, new string [] { "Lock" });
					return type;
				case "FOUR_LEVEL_ALPHABETIC":
					type.ModifierNames = new string [] { "Shift", "Lock", "LevelThree" };
					AddTypeMap (type, EmptyStringArray, 0);
					AddTypeMap (type, new string [] { "Shift" }, 1);
					AddTypeMap (type, new string [] { "Lock" }, 1);
					AddTypeMap (type, new string [] { "Shift", "Lock" }, 0);
					AddTypeMap (type, new string [] { "LevelThree" }, 2);
					AddTypeMap (type, new string [] { "Shift", "LevelThree" }, 3);
					AddTypeMap (type, new string [] { "Lock", "LevelThree" }, 3);
					AddTypeMap (type, new string [] { "Shift", "Lock", "LevelThree" }, 2);
					return type;
				case "KEYPAD":
					type.ModifierNames = new string [] { "Shift", "NumLock" };
					AddTypeMap (type, EmptyStringArray, 0);
					AddTypeMap (type, new string [] { "NumLock" }, 1);
					AddTypeMap (type, new string [] { "Shift", "NumLock" }, 0);
					return type;
				case "FOUR_LEVEL_KEYPAD":
					type.ModifierNames = new string [] { "Shift", "NumLock", "LevelThree" };
					AddTypeMap (type, EmptyStringArray, 0);
					AddTypeMap (type, new string [] { "Shift" }, 1);
					AddTypeMap (type, new string [] { "NumLock" }, 1);
					AddTypeMap (type, new string [] { "Shift", "NumLock" }, 0);
					AddTypeMap (type, new string [] { "LevelThree" }, 2);
					AddTypeMap (type, new string [] { "Shift", "LevelThree" }, 3);
					AddTypeMap (type, new string [] { "NumLock", "LevelThree" }, 3);
					AddTypeMap (type, new string [] { "Shift", "NumLock", "LevelThree" }, 2);
					return type;
				default:
					return null;
				}
			}

			static void AddTypeMap (XkbType type, string [] modifiers, int level)
			{
				XkbTypeMap map = new XkbTypeMap ();
				map.ModifierNames = modifiers;
				map.Level = level;
				type.Maps.Add (map);
			}

			static void AddTypePreserve (XkbType type, string [] modifiers, string [] preserve)
			{
				XkbTypePreserve entry = new XkbTypePreserve ();
				entry.ModifierNames = modifiers;
				entry.PreserveNames = preserve;
				type.Preserves.Add (entry);
			}

			static string InferTypeName (XkbSymbol [] symbols)
			{
				int length = NormalizedSymbolLength (symbols);
				switch (length) {
				case 0:
				case 1:
					return "ONE_LEVEL";
				case 2:
					if (IsLowerUpperLetterPair (symbols [0], symbols [1]))
						return "ALPHABETIC";
					if (IsKeypadSymbol (symbols [0]) || IsKeypadSymbol (symbols [1]))
						return "KEYPAD";
					return "TWO_LEVEL";
				case 3:
				case 4:
					if (IsLowerUpperLetterPair (symbols [0], symbols [1])) {
						if (IsLowerUpperLetterPair (symbols [2], SymbolAt (symbols, 3)))
							return "FOUR_LEVEL_ALPHABETIC";
						return "FOUR_LEVEL_SEMIALPHABETIC";
					}
					if (IsKeypadSymbol (symbols [0]) || IsKeypadSymbol (symbols [1]))
						return "FOUR_LEVEL_KEYPAD";
					return "FOUR_LEVEL";
				default:
					return "ONE_LEVEL";
				}
			}

			static int NormalizedSymbolLength (XkbSymbol [] symbols)
			{
				int length = symbols == null ? 0 : symbols.Length;
				while (length > 0 && symbols [length - 1].NoSymbol)
					length--;
				return length;
			}

			static XkbSymbol SymbolAt (XkbSymbol [] symbols, int index)
			{
				if (symbols == null || index < 0 || index >= symbols.Length)
					return NoSymbol ();
				return symbols [index];
			}

			static bool IsLowerUpperLetterPair (XkbSymbol lower, XkbSymbol upper)
			{
				return IsLowerLetterSymbol (lower) && IsUpperLetterSymbol (upper);
			}

			static bool IsLowerLetterSymbol (XkbSymbol symbol)
			{
				if (String.IsNullOrEmpty (symbol.Text) || symbol.Text.Length != 1)
					return false;
				char c = symbol.Text [0];
				return Char.IsLetter (c) && Char.ToLowerInvariant (c) == c && Char.ToUpperInvariant (c) != c;
			}

			static bool IsUpperLetterSymbol (XkbSymbol symbol)
			{
				if (String.IsNullOrEmpty (symbol.Text) || symbol.Text.Length != 1)
					return false;
				char c = symbol.Text [0];
				return Char.IsLetter (c) && Char.ToUpperInvariant (c) == c && Char.ToLowerInvariant (c) != c;
			}

			static bool IsKeypadSymbol (XkbSymbol symbol)
			{
				return symbol.Keysym >= XkbKeysymKpEnter && symbol.Keysym <= XkbKeysymKpDivide ||
					symbol.Keysym >= XkbKeysymKp0 && symbol.Keysym <= XkbKeysymKp9;
			}

			static bool TryCreateSymbol (Token token, out XkbSymbol symbol)
			{
				if (token.Kind == TokenKind.String) {
					symbol = SymbolFromString (token.Text);
					return true;
				}

				if (token.Kind == TokenKind.Number) {
					if (!String.IsNullOrEmpty (token.Text) && token.Text.Length == 1 && Char.IsDigit (token.Text [0]))
						return TryCreateSymbol (token.Text, out symbol);
					symbol = SymbolFromNumericKeysym (token.Number, token.Text);
					return true;
				}

				return TryCreateSymbol (TokenText (token), out symbol);
			}

			static bool TryCreateSymbol (string name, out XkbSymbol symbol)
			{
				symbol = NoSymbol ();
				if (String.IsNullOrEmpty (name) || name == "NoSymbol" || name == "VoidSymbol")
					return true;

				uint keysym;
				if (!TryKeysymFromName (name, out keysym)) {
					if (name.StartsWith ("dead_", StringComparison.Ordinal)) {
						symbol.NoSymbol = false;
						symbol.Name = name;
						symbol.Keysym = XkbKeysymIsoSpecialStart;
						return true;
					}
					return false;
				}

				symbol.NoSymbol = false;
				symbol.Name = name;
				symbol.Keysym = keysym;
				symbol.Text = TextFromKeysym (keysym);
				return true;
			}

			static XkbSymbol SymbolFromString (string text)
			{
				if (String.IsNullOrEmpty (text))
					return NoSymbol ();

				uint codepoint;
				if (!TryReadCodepoint (text, 0, out codepoint))
					return NoSymbol ();

				XkbSymbol symbol = SymbolFromCodepoint (codepoint, text);
				symbol.Text = text;
				return symbol;
			}

			static XkbSymbol SymbolFromNumericKeysym (uint keysym, string name)
			{
				XkbSymbol symbol = NoSymbol ();
				if (keysym == 0)
					return symbol;
				symbol.NoSymbol = false;
				symbol.Name = name;
				symbol.Keysym = keysym;
				symbol.Text = TextFromKeysym (keysym);
				return symbol;
			}

			static XkbSymbol SymbolFromCodepoint (uint codepoint, string name)
			{
				XkbSymbol symbol = NoSymbol ();
				if (codepoint == 0 || codepoint > UnicodeMaxCodePoint)
					return symbol;
				symbol.NoSymbol = false;
				symbol.Name = name;
				symbol.Keysym = codepoint <= 0xff ? codepoint : XkbKeysymUnicodePrefix | codepoint;
				symbol.Text = TextFromKeysym (symbol.Keysym);
				return symbol;
			}

			static bool TryReadCodepoint (string text, int index, out uint codepoint)
			{
				codepoint = 0;
				if (index < 0 || index >= text.Length)
					return false;
				char c = text [index];
				if (Char.IsHighSurrogate (c)) {
					if (index + 1 >= text.Length || !Char.IsLowSurrogate (text [index + 1]))
						return false;
					codepoint = (uint) Char.ConvertToUtf32 (c, text [index + 1]);
					return true;
				}
				if (Char.IsLowSurrogate (c))
					return false;
				codepoint = c;
				return true;
			}

			static XkbSymbol NoSymbol ()
			{
				XkbSymbol symbol = new XkbSymbol ();
				symbol.NoSymbol = true;
				return symbol;
			}

			static int CountPrintableKeys (Dictionary<uint, Key> keys)
			{
				int count = 0;
				foreach (Key key in keys.Values) {
					if (key.Groups == null)
						continue;
					bool hasText = false;
					for (int group = 0; group < key.Groups.Length && !hasText; group++) {
						KeyGroup keyGroup = key.Groups [group];
						if (keyGroup == null || keyGroup.Symbols == null)
							continue;
						for (int level = 0; level < keyGroup.Symbols.Length; level++) {
							if (!String.IsNullOrEmpty (keyGroup.Symbols [level].Text)) {
								hasText = true;
								break;
							}
						}
					}
					if (hasText)
						count++;
				}
				return count;
			}

			static readonly Dictionary<string, uint> namedKeysyms = CreateNamedKeysyms ();

			static bool TryKeysymFromName (string name, out uint keysym)
			{
				keysym = 0;
				if (String.IsNullOrEmpty (name) || name == "VoidSymbol")
					return false;

				if (name.Length == 1) {
					keysym = name [0];
					return true;
				}

				if (name.Length > 1 && name [0] == 'U') {
					uint codepoint;
					if (TryParseHex (name, 1, out codepoint) && codepoint <= UnicodeMaxCodePoint) {
						keysym = XkbKeysymUnicodePrefix | codepoint;
						return true;
					}
				}

				if (name.Length > 1 && name [0] == '0' && (name [1] == 'x' || name [1] == 'X'))
					return TryParseHex (name, 2, out keysym);

				if (name.Length > 1 && name [0] == 'F') {
					uint functionNumber;
					if (UInt32.TryParse (name.Substring (1), out functionNumber) && functionNumber >= 1 && functionNumber <= 35) {
						keysym = XkbKeysymF1 + functionNumber - 1;
						return true;
					}
				}

				return namedKeysyms.TryGetValue (name, out keysym);
			}

			static bool TryParseHex (string text, int offset, out uint value)
			{
				value = 0;
				if (offset >= text.Length)
					return false;

				for (int i = offset; i < text.Length; i++) {
					char c = text [i];
					uint digit;
					if (c >= '0' && c <= '9')
						digit = (uint) (c - '0');
					else if (c >= 'a' && c <= 'f')
						digit = (uint) (c - 'a' + 10);
					else if (c >= 'A' && c <= 'F')
						digit = (uint) (c - 'A' + 10);
					else
						return false;
					value = (value << 4) | digit;
				}

				return true;
			}

			static string TextFromKeysym (uint keysym)
			{
				switch (keysym) {
				case XkbKeysymBackSpace: return "\b";
				case XkbKeysymTab: return "\t";
				case XkbKeysymReturn: return "\r";
				}

				if (keysym >= XkbKeysymIsoSpecialStart && keysym <= XkbKeysymSpecialEnd)
					return null;

				uint codepoint = keysym;
				if ((keysym & XkbKeysymUnicodePrefixMask) == XkbKeysymUnicodePrefix)
					codepoint = keysym & XkbKeysymUnicodeCodepointMask;
				if (codepoint == 0 || codepoint > UnicodeMaxCodePoint)
					return null;
				return Char.ConvertFromUtf32 ((int) codepoint);
			}

			static Keys KeysFromModifierMask (uint mask)
			{
				Keys keys = Keys.None;
				if ((mask & XkbModifierShiftMask) != 0)
					keys |= Keys.Shift;
				if ((mask & XkbModifierControlMask) != 0)
					keys |= Keys.Control;
				if ((mask & XkbModifierMod1Mask) != 0)
					keys |= Keys.Alt;
				return keys;
			}

			static bool IsNoModifierName (string name)
			{
				return String.Equals (name, "none", StringComparison.OrdinalIgnoreCase);
			}

			static bool IsUnsupportedModifierWildcard (string name)
			{
				return String.Equals (name, "all", StringComparison.OrdinalIgnoreCase) ||
					String.Equals (name, "any", StringComparison.OrdinalIgnoreCase);
			}

			static Dictionary<string, uint> CreateNamedKeysyms ()
			{
				Dictionary<string, uint> keysyms = new Dictionary<string, uint> (StringComparer.Ordinal);

				AddAsciiKeysyms (keysyms);
				AddLatin1Keysyms (keysyms);
				AddControlKeysyms (keysyms);
				AddKeypadKeysyms (keysyms);

				keysyms ["EuroSign"] = 0x20ac;
				keysyms ["OE"] = 0x0152;
				keysyms ["oe"] = 0x0153;
				keysyms ["Ydiaeresis"] = 0x0178;
				return keysyms;
			}

			static void AddAsciiKeysyms (Dictionary<string, uint> keysyms)
			{
				keysyms ["space"] = ' ';
				keysyms ["exclam"] = '!';
				keysyms ["quotedbl"] = '"';
				keysyms ["numbersign"] = '#';
				keysyms ["dollar"] = '$';
				keysyms ["percent"] = '%';
				keysyms ["ampersand"] = '&';
				keysyms ["apostrophe"] = '\'';
				keysyms ["quoteright"] = '\'';
				keysyms ["parenleft"] = '(';
				keysyms ["parenright"] = ')';
				keysyms ["asterisk"] = '*';
				keysyms ["plus"] = '+';
				keysyms ["comma"] = ',';
				keysyms ["minus"] = '-';
				keysyms ["period"] = '.';
				keysyms ["slash"] = '/';
				keysyms ["colon"] = ':';
				keysyms ["semicolon"] = ';';
				keysyms ["less"] = '<';
				keysyms ["equal"] = '=';
				keysyms ["greater"] = '>';
				keysyms ["question"] = '?';
				keysyms ["at"] = '@';
				keysyms ["bracketleft"] = '[';
				keysyms ["backslash"] = '\\';
				keysyms ["bracketright"] = ']';
				keysyms ["asciicircum"] = '^';
				keysyms ["underscore"] = '_';
				keysyms ["grave"] = '`';
				keysyms ["quoteleft"] = '`';
				keysyms ["braceleft"] = '{';
				keysyms ["bar"] = '|';
				keysyms ["braceright"] = '}';
				keysyms ["asciitilde"] = '~';
			}

			static void AddLatin1Keysyms (Dictionary<string, uint> keysyms)
			{
				keysyms ["nobreakspace"] = 0x00a0;
				keysyms ["exclamdown"] = 0x00a1;
				keysyms ["cent"] = 0x00a2;
				keysyms ["sterling"] = 0x00a3;
				keysyms ["currency"] = 0x00a4;
				keysyms ["yen"] = 0x00a5;
				keysyms ["brokenbar"] = 0x00a6;
				keysyms ["section"] = 0x00a7;
				keysyms ["diaeresis"] = 0x00a8;
				keysyms ["copyright"] = 0x00a9;
				keysyms ["ordfeminine"] = 0x00aa;
				keysyms ["guillemotleft"] = 0x00ab;
				keysyms ["notsign"] = 0x00ac;
				keysyms ["hyphen"] = 0x00ad;
				keysyms ["registered"] = 0x00ae;
				keysyms ["macron"] = 0x00af;
				keysyms ["degree"] = 0x00b0;
				keysyms ["plusminus"] = 0x00b1;
				keysyms ["twosuperior"] = 0x00b2;
				keysyms ["threesuperior"] = 0x00b3;
				keysyms ["acute"] = 0x00b4;
				keysyms ["mu"] = 0x00b5;
				keysyms ["paragraph"] = 0x00b6;
				keysyms ["periodcentered"] = 0x00b7;
				keysyms ["cedilla"] = 0x00b8;
				keysyms ["onesuperior"] = 0x00b9;
				keysyms ["masculine"] = 0x00ba;
				keysyms ["guillemotright"] = 0x00bb;
				keysyms ["onequarter"] = 0x00bc;
				keysyms ["onehalf"] = 0x00bd;
				keysyms ["threequarters"] = 0x00be;
				keysyms ["questiondown"] = 0x00bf;
				AddLatinPair (keysyms, "Agrave", "agrave", 0x00c0, 0x00e0);
				AddLatinPair (keysyms, "Aacute", "aacute", 0x00c1, 0x00e1);
				AddLatinPair (keysyms, "Acircumflex", "acircumflex", 0x00c2, 0x00e2);
				AddLatinPair (keysyms, "Atilde", "atilde", 0x00c3, 0x00e3);
				AddLatinPair (keysyms, "Adiaeresis", "adiaeresis", 0x00c4, 0x00e4);
				AddLatinPair (keysyms, "Aring", "aring", 0x00c5, 0x00e5);
				AddLatinPair (keysyms, "AE", "ae", 0x00c6, 0x00e6);
				AddLatinPair (keysyms, "Ccedilla", "ccedilla", 0x00c7, 0x00e7);
				AddLatinPair (keysyms, "Egrave", "egrave", 0x00c8, 0x00e8);
				AddLatinPair (keysyms, "Eacute", "eacute", 0x00c9, 0x00e9);
				AddLatinPair (keysyms, "Ecircumflex", "ecircumflex", 0x00ca, 0x00ea);
				AddLatinPair (keysyms, "Ediaeresis", "ediaeresis", 0x00cb, 0x00eb);
				AddLatinPair (keysyms, "Igrave", "igrave", 0x00cc, 0x00ec);
				AddLatinPair (keysyms, "Iacute", "iacute", 0x00cd, 0x00ed);
				AddLatinPair (keysyms, "Icircumflex", "icircumflex", 0x00ce, 0x00ee);
				AddLatinPair (keysyms, "Idiaeresis", "idiaeresis", 0x00cf, 0x00ef);
				keysyms ["ETH"] = 0x00d0;
				keysyms ["eth"] = 0x00f0;
				AddLatinPair (keysyms, "Ntilde", "ntilde", 0x00d1, 0x00f1);
				AddLatinPair (keysyms, "Ograve", "ograve", 0x00d2, 0x00f2);
				AddLatinPair (keysyms, "Oacute", "oacute", 0x00d3, 0x00f3);
				AddLatinPair (keysyms, "Ocircumflex", "ocircumflex", 0x00d4, 0x00f4);
				AddLatinPair (keysyms, "Otilde", "otilde", 0x00d5, 0x00f5);
				AddLatinPair (keysyms, "Odiaeresis", "odiaeresis", 0x00d6, 0x00f6);
				keysyms ["multiply"] = 0x00d7;
				AddLatinPair (keysyms, "Oslash", "oslash", 0x00d8, 0x00f8);
				AddLatinPair (keysyms, "Ugrave", "ugrave", 0x00d9, 0x00f9);
				AddLatinPair (keysyms, "Uacute", "uacute", 0x00da, 0x00fa);
				AddLatinPair (keysyms, "Ucircumflex", "ucircumflex", 0x00db, 0x00fb);
				AddLatinPair (keysyms, "Udiaeresis", "udiaeresis", 0x00dc, 0x00fc);
				AddLatinPair (keysyms, "Yacute", "yacute", 0x00dd, 0x00fd);
				keysyms ["THORN"] = 0x00de;
				keysyms ["thorn"] = 0x00fe;
				keysyms ["ssharp"] = 0x00df;
				keysyms ["division"] = 0x00f7;
				keysyms ["ydiaeresis"] = 0x00ff;
			}

			static void AddLatinPair (Dictionary<string, uint> keysyms, string upperName, string lowerName, uint upper, uint lower)
			{
				keysyms [upperName] = upper;
				keysyms [lowerName] = lower;
			}

			static void AddControlKeysyms (Dictionary<string, uint> keysyms)
			{
				keysyms ["BackSpace"] = XkbKeysymBackSpace;
				keysyms ["Tab"] = XkbKeysymTab;
				keysyms ["ISO_Left_Tab"] = XkbKeysymTab;
				keysyms ["Return"] = XkbKeysymReturn;
				keysyms ["Pause"] = XkbKeysymPause;
				keysyms ["Scroll_Lock"] = XkbKeysymScrollLock;
				keysyms ["Escape"] = XkbKeysymEscape;
				keysyms ["Home"] = XkbKeysymHome;
				keysyms ["Left"] = XkbKeysymLeft;
				keysyms ["Up"] = XkbKeysymUp;
				keysyms ["Right"] = XkbKeysymRight;
				keysyms ["Down"] = XkbKeysymDown;
				keysyms ["Prior"] = XkbKeysymPageUp;
				keysyms ["Page_Up"] = XkbKeysymPageUp;
				keysyms ["Next"] = XkbKeysymPageDown;
				keysyms ["Page_Down"] = XkbKeysymPageDown;
				keysyms ["End"] = XkbKeysymEnd;
				keysyms ["Print"] = XkbKeysymPrint;
				keysyms ["Insert"] = XkbKeysymInsert;
				keysyms ["Menu"] = XkbKeysymMenu;
				keysyms ["Help"] = XkbKeysymHelp;
				keysyms ["Break"] = XkbKeysymBreak;
				keysyms ["Num_Lock"] = XkbKeysymNumLock;
				keysyms ["Delete"] = XkbKeysymDelete;
				keysyms ["Shift_L"] = XkbKeysymShiftL;
				keysyms ["Shift_R"] = XkbKeysymShiftR;
				keysyms ["Control_L"] = XkbKeysymControlL;
				keysyms ["Control_R"] = XkbKeysymControlR;
				keysyms ["Caps_Lock"] = XkbKeysymCapsLock;
				keysyms ["Meta_L"] = XkbKeysymMetaL;
				keysyms ["Meta_R"] = XkbKeysymMetaR;
				keysyms ["Alt_L"] = XkbKeysymAltL;
				keysyms ["Alt_R"] = XkbKeysymAltR;
				keysyms ["Super_L"] = XkbKeysymSuperL;
				keysyms ["Super_R"] = XkbKeysymSuperR;
				keysyms ["Hyper_L"] = XkbKeysymHyperL;
				keysyms ["Hyper_R"] = XkbKeysymHyperR;
				keysyms ["Mode_switch"] = XkbKeysymModeSwitch;
				keysyms ["ISO_Level3_Shift"] = XkbKeysymIsoLevel3Shift;
			}

			static void AddKeypadKeysyms (Dictionary<string, uint> keysyms)
			{
				keysyms ["KP_Enter"] = XkbKeysymKpEnter;
				keysyms ["KP_Multiply"] = XkbKeysymKpMultiply;
				keysyms ["KP_Add"] = XkbKeysymKpAdd;
				keysyms ["KP_Separator"] = XkbKeysymKpSeparator;
				keysyms ["KP_Subtract"] = XkbKeysymKpSubtract;
				keysyms ["KP_Decimal"] = XkbKeysymKpDecimal;
				keysyms ["KP_Divide"] = XkbKeysymKpDivide;
				for (uint i = 0; i <= 9; i++)
					keysyms ["KP_" + i.ToString ()] = XkbKeysymKp0 + i;
				for (uint i = 1; i <= 35; i++)
					keysyms ["F" + i.ToString ()] = XkbKeysymF1 + i - 1;
			}

			static uint ModifierMaskForName (string name)
			{
				switch (name) {
				case "Shift": return XkbModifierShiftMask;
				case "Lock": return XkbModifierLockMask;
				case "Control": return XkbModifierControlMask;
				case "Mod1": return XkbModifierMod1Mask;
				case "Mod2": return XkbModifierMod2Mask;
				case "Mod3": return XkbModifierMod3Mask;
				case "Mod4": return XkbModifierMod4Mask;
				case "Mod5": return XkbModifierMod5Mask;
				default: return 0;
				}
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
			uint activeModifiers;
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
				if (waylandKeymap.Format != WaylandProtocol.WlKeyboard.KeymapFormatXkbV1) {
					diagnostic = "unsupported Wayland keymap format " + waylandKeymap.Format.ToString ();
					return null;
				}
				if (String.IsNullOrEmpty (waylandKeymap.Text)) {
					diagnostic = "empty xkb_v1 keymap text";
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
				activeModifiers = depressed | latched | locked;
				if (IsMaskActive (activeModifiers, shiftIndex))
					keys |= Keys.Shift;
				if (IsMaskActive (activeModifiers, controlIndex))
					keys |= Keys.Control;
				if (IsMaskActive (activeModifiers, altIndex))
					keys |= Keys.Alt;
				modifierKeys = keys;
			}

			public WaylandKeyResult TranslateKey (uint evdevKey, bool pressed)
			{
				uint xkbKeycode = evdevKey + XkbKeycodeOffset;
				WaylandKeyResult result = new WaylandKeyResult ();
				uint consumedModifiers = xkb_state_key_get_consumed_mods (state, xkbKeycode);
				result.ShortcutModifiers = KeysFromNativeMask (activeModifiers & ~consumedModifiers);
				result.HasShortcutModifiers = true;
				uint keysym = xkb_state_key_get_one_sym (state, xkbKeycode);
				result.KeyCode = MapKeysymToKeys (keysym);
				if (result.KeyCode == Keys.None)
					result.KeyCode = MapEvdevKey (evdevKey);
				if (pressed && (result.ShortcutModifiers & Keys.Control) == 0)
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

			Keys KeysFromNativeMask (uint mask)
			{
				Keys keys = Keys.None;
				if (IsMaskActive (mask, shiftIndex))
					keys |= Keys.Shift;
				if (IsMaskActive (mask, controlIndex))
					keys |= Keys.Control;
				if (IsMaskActive (mask, altIndex))
					keys |= Keys.Alt;
				return keys;
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
			static extern uint xkb_state_key_get_consumed_mods (IntPtr state, uint keycode);

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
		readonly Dictionary<uint, WaylandDataOffer> dataOffers = new Dictionary<uint, WaylandDataOffer> ();
		readonly Dictionary<uint, ClipboardSelection> dataSources = new Dictionary<uint, ClipboardSelection> ();
		readonly Dictionary<string, int> clipboardFormatIds = new Dictionary<string, int> (StringComparer.Ordinal);
		readonly ClipboardSelection clipboardSelection = new ClipboardSelection ();
		readonly ClipboardSelection primarySelection = new ClipboardSelection ();
		readonly List<IntPtr> zOrder = new List<IntPtr> ();
		readonly List<Timer> timers = new List<Timer> ();
		readonly Bitmap fallbackBitmap = new Bitmap (1, 1, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
		readonly HashSet<uint> evdevKeysDown = new HashSet<uint> ();
		readonly Dictionary<uint, bool> keySysState = new Dictionary<uint, bool> ();
		readonly Dictionary<string, string> keyText = new Dictionary<string, string> ();
		readonly WaylandCaret caret = new WaylandCaret ();
		IWaylandKeyboardLayout keyboardLayout = new PhysicalUsKeyboardLayout ();
		bool menuState;

		WaylandConnection connection;
		WaylandRegistry registry;
		uint compositorId;
		uint subcompositorId;
		uint shmId;
		uint xdgWmBaseId;
		uint seatId;
		uint pointerId;
		uint keyboardId;
		uint dataDeviceManagerId;
		uint dataDeviceId;
		uint selectionOfferId;
		uint cursorShapeManagerId;
		uint cursorShapeDeviceId;
		uint lastInputSerial;
		uint pointerEnterSerial;
		uint cursorSurfaceId;
		int nextHandle = 0x4000;
		int nextCursorHandle = 0x800000;
		int nextClipboardFormatId = ClipboardCustomFormatStart;
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
			dataDeviceManagerId = connection.Bind (registry, "wl_data_device_manager", 3);
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
			if (seatId != 0 && dataDeviceManagerId != 0)
				CreateDataDevice ();

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
				DestroySelectionSource (closingConnection, clipboardSelection);
				DestroySelectionSource (closingConnection, primarySelection);
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
			dataOffers.Clear ();
			dataSources.Clear ();
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
			// This mirrors Mono's X11Keyboard.menu_state.  A bare Alt
			// press/release is menu activation, but an Alt chord is not: after
			// Alt+key, the later Alt release must be delivered as a normal key-up
			// so Mono does not activate menu keyboard capture after the chord.
			if (msg.message == Msg.WM_SYSKEYUP && (Keys) msg.wParam.ToInt32 () == Keys.Menu && menuState) {
				msg.message = Msg.WM_KEYUP;
				menuState = false;
			}

			if (msg.message == Msg.WM_SYSKEYDOWN && ((Keys) msg.wParam.ToInt32 () & Keys.KeyCode) != Keys.Menu)
				menuState = true;

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
			ClipboardSelection selection = GetClipboardSelection (handle);
			if (selection.Dirty)
				PublishClipboardSelection (selection, handle == ClipboardHandle);
		}

		internal override IntPtr ClipboardOpen (bool primarySelection)
		{
			return primarySelection ? PrimarySelectionHandle : ClipboardHandle;
		}

		internal override int ClipboardGetID (IntPtr handle, string format)
		{
			GetClipboardSelection (handle);

			int id;
			if (TryGetStandardClipboardFormatId (format, out id))
				return id;

			if (!clipboardFormatIds.TryGetValue (format, out id)) {
				id = nextClipboardFormatId++;
				clipboardFormatIds [format] = id;
			}

			return id;
		}

		internal override void ClipboardStore (IntPtr handle, object obj, int id, XplatUI.ObjectToClipboard converter, bool copy)
		{
			ClipboardSelection selection = GetClipboardSelection (handle);
			if (obj == null) {
				ClearClipboardSelection (selection, handle == ClipboardHandle);
				return;
			}

			selection.Data [NormalizeClipboardFormatId (handle, id, obj)] = obj;
			selection.OwnsSelection = true;
			selection.Dirty = true;
		}

		internal override int [] ClipboardAvailableFormats (IntPtr handle)
		{
			ClipboardSelection selection = GetClipboardSelection (handle);
			if (selection.OwnsSelection && selection.Data.Count > 0) {
				int [] formats = new int [selection.Data.Count];
				selection.Data.Keys.CopyTo (formats, 0);
				return formats;
			}

			WaylandDataOffer offer;
			if (handle == ClipboardHandle && selectionOfferId != 0 && dataOffers.TryGetValue (selectionOfferId, out offer))
				return GetFormatsForOffer (offer);

			return null;
		}

		internal override object ClipboardRetrieve (IntPtr handle, int id, XplatUI.ClipboardToObject converter)
		{
			ClipboardSelection selection = GetClipboardSelection (handle);
			object obj;
			if (selection.OwnsSelection && selection.Data.TryGetValue (id, out obj))
				return obj;

			WaylandDataOffer offer;
			if (handle == ClipboardHandle && selectionOfferId != 0 && dataOffers.TryGetValue (selectionOfferId, out offer))
				return RetrieveFromOffer (offer, id);

			return null;
		}

		void CreateDataDevice ()
		{
			dataDeviceId = connection.AllocateId ();
			connection.SendRequest (dataDeviceManagerId, WaylandProtocol.WlDataDeviceManager.GetDataDevice, delegate (WaylandRequestBuilder b) {
				b.WriteNewId (dataDeviceId);
				b.WriteObject (seatId);
			});
		}

		ClipboardSelection GetClipboardSelection (IntPtr handle)
		{
			if (handle == ClipboardHandle)
				return clipboardSelection;
			if (handle == PrimarySelectionHandle)
				return primarySelection;
			throw new ArgumentException ("handle is not a valid clipboard handle");
		}

		int NormalizeClipboardFormatId (IntPtr handle, int id, object obj)
		{
			if (id != -1)
				return id;

			if (obj is string)
				return ClipboardGetID (handle, DataFormats.UnicodeText);
			if (obj is Image)
				return ClipboardGetID (handle, DataFormats.Dib);
			return ClipboardGetID (handle, obj.GetType ().FullName);
		}

		bool TryGetStandardClipboardFormatId (string format, out int id)
		{
			if (format == DataFormats.Text) {
				id = ClipboardFormatText;
				return true;
			}
			if (format == DataFormats.Bitmap) {
				id = ClipboardFormatBitmap;
				return true;
			}
			if (format == DataFormats.MetafilePict) {
				id = ClipboardFormatMetafilePict;
				return true;
			}
			if (format == DataFormats.SymbolicLink) {
				id = ClipboardFormatSymbolicLink;
				return true;
			}
			if (format == DataFormats.Dif) {
				id = ClipboardFormatDif;
				return true;
			}
			if (format == DataFormats.Tiff) {
				id = ClipboardFormatTiff;
				return true;
			}
			if (format == DataFormats.OemText) {
				id = ClipboardFormatOemText;
				return true;
			}
			if (format == DataFormats.Dib) {
				id = ClipboardFormatDib;
				return true;
			}
			if (format == DataFormats.Palette) {
				id = ClipboardFormatPalette;
				return true;
			}
			if (format == DataFormats.PenData) {
				id = ClipboardFormatPenData;
				return true;
			}
			if (format == DataFormats.Riff) {
				id = ClipboardFormatRiff;
				return true;
			}
			if (format == DataFormats.WaveAudio) {
				id = ClipboardFormatWaveAudio;
				return true;
			}
			if (format == DataFormats.UnicodeText) {
				id = ClipboardFormatUnicodeText;
				return true;
			}
			if (format == DataFormats.EnhancedMetafile) {
				id = ClipboardFormatEnhancedMetafile;
				return true;
			}
			if (format == DataFormats.FileDrop) {
				id = ClipboardFormatFileDrop;
				return true;
			}
			if (format == DataFormats.Locale) {
				id = ClipboardFormatLocale;
				return true;
			}

			id = 0;
			return false;
		}

		void ClearClipboardSelection (ClipboardSelection selection, bool waylandClipboard)
		{
			selection.Data.Clear ();
			selection.Dirty = false;
			selection.OwnsSelection = false;

			if (!waylandClipboard)
				return;

			if (dataDeviceId == 0)
				return;

			WaylandConnection liveConnection = RequireConnection ();
			DestroySelectionSource (liveConnection, selection);
			if (lastInputSerial != 0) {
				liveConnection.SendRequest (dataDeviceId, WaylandProtocol.WlDataDevice.SetSelection, delegate (WaylandRequestBuilder b) {
					b.WriteObject (0);
					b.WriteUInt32 (lastInputSerial);
				});
			}
		}

		void PublishClipboardSelection (ClipboardSelection selection, bool waylandClipboard)
		{
			selection.Dirty = false;
			selection.OwnsSelection = selection.Data.Count > 0;
			if (!waylandClipboard || !selection.OwnsSelection || dataDeviceId == 0)
				return;

			List<string> mimeTypes = GetMimeTypesForSelection (selection);
			if (mimeTypes.Count == 0)
				return;

			// Wayland selection ownership is tied to the input serial that caused
			// the copy.  Programmatic clipboard writes without such a serial still
			// work through the local cache, but are not valid compositor selection
			// claims.
			if (lastInputSerial == 0)
				return;

			WaylandConnection liveConnection = RequireConnection ();
			DestroySelectionSource (liveConnection, selection);

			uint sourceId = liveConnection.AllocateId ();
			liveConnection.SendRequest (dataDeviceManagerId, WaylandProtocol.WlDataDeviceManager.CreateDataSource, delegate (WaylandRequestBuilder b) {
				b.WriteNewId (sourceId);
			});
			foreach (string mimeType in mimeTypes) {
				liveConnection.SendRequest (sourceId, WaylandProtocol.WlDataSource.Offer, delegate (WaylandRequestBuilder b) {
					b.WriteString (mimeType);
				});
			}
			liveConnection.SendRequest (dataDeviceId, WaylandProtocol.WlDataDevice.SetSelection, delegate (WaylandRequestBuilder b) {
				b.WriteObject (sourceId);
				b.WriteUInt32 (lastInputSerial);
			});

			selection.SourceId = sourceId;
			dataSources [sourceId] = selection;
		}

		void DestroySelectionSource (WaylandConnection liveConnection, ClipboardSelection selection)
		{
			if (selection.SourceId == 0)
				return;

			uint sourceId = selection.SourceId;
			selection.SourceId = 0;
			dataSources.Remove (sourceId);
			liveConnection.SendRequest (sourceId, WaylandProtocol.WlDataSource.Destroy, null);
		}

		List<string> GetMimeTypesForSelection (ClipboardSelection selection)
		{
			List<string> mimeTypes = new List<string> ();
			foreach (KeyValuePair<int, object> item in selection.Data)
				AddMimeTypesForFormat (mimeTypes, item.Key, item.Value);
			return mimeTypes;
		}

		void AddMimeTypesForFormat (List<string> mimeTypes, int id, object obj)
		{
			if (!(obj is string))
				return;

			switch (id) {
			case ClipboardFormatText:
			case ClipboardFormatUnicodeText:
			case ClipboardFormatOemText:
				AddUniqueMimeType (mimeTypes, MimeTextUtf8);
				AddUniqueMimeType (mimeTypes, MimeTextPlain);
				break;
			default:
				DataFormats.Format format = DataFormats.GetFormat (id);
				if (format != null && format.Name == DataFormats.StringFormat) {
					AddUniqueMimeType (mimeTypes, MimeTextUtf8);
					AddUniqueMimeType (mimeTypes, MimeTextPlain);
				} else if (format != null && format.Name == DataFormats.Html) {
					AddUniqueMimeType (mimeTypes, MimeHtml);
				} else if (format != null && format.Name == DataFormats.Rtf) {
					AddUniqueMimeType (mimeTypes, MimeRtf);
				}
				break;
			}
		}

		void AddUniqueMimeType (List<string> mimeTypes, string mimeType)
		{
			if (!mimeTypes.Contains (mimeType))
				mimeTypes.Add (mimeType);
		}

		int [] GetFormatsForOffer (WaylandDataOffer offer)
		{
			List<int> formats = new List<int> ();
			foreach (string mimeType in offer.MimeTypes) {
				if (IsTextMimeType (mimeType)) {
					AddUniqueFormat (formats, ClipboardFormatUnicodeText);
					AddUniqueFormat (formats, ClipboardFormatText);
				} else if (String.Equals (mimeType, MimeHtml, StringComparison.OrdinalIgnoreCase)) {
					AddUniqueFormat (formats, DataFormats.GetFormat (DataFormats.Html).Id);
				} else if (String.Equals (mimeType, MimeRtf, StringComparison.OrdinalIgnoreCase)) {
					AddUniqueFormat (formats, DataFormats.GetFormat (DataFormats.Rtf).Id);
				}
			}

			if (formats.Count == 0)
				return null;
			return formats.ToArray ();
		}

		void AddUniqueFormat (List<int> formats, int format)
		{
			if (!formats.Contains (format))
				formats.Add (format);
		}

		object RetrieveFromOffer (WaylandDataOffer offer, int id)
		{
			string mimeType = ChooseMimeTypeForFormat (offer, id);
			if (mimeType == null)
				return null;

			byte [] data = ReceiveOfferData (offer, mimeType);
			if (data == null)
				return null;

			if (id == ClipboardFormatText || id == ClipboardFormatUnicodeText || IsKnownTextFormat (id))
				return DecodeClipboardText (data);

			return null;
		}

		bool IsKnownTextFormat (int id)
		{
			DataFormats.Format format = DataFormats.GetFormat (id);
			return format != null && (format.Name == DataFormats.StringFormat || format.Name == DataFormats.Html || format.Name == DataFormats.Rtf);
		}

		string ChooseMimeTypeForFormat (WaylandDataOffer offer, int id)
		{
			DataFormats.Format format = DataFormats.GetFormat (id);
			if (id == ClipboardFormatText || id == ClipboardFormatUnicodeText ||
			    (format != null && format.Name == DataFormats.StringFormat))
				return FindTextMimeType (offer);
			if (format != null && format.Name == DataFormats.Html && OfferHasMimeType (offer, MimeHtml))
				return MimeHtml;
			if (format != null && format.Name == DataFormats.Rtf && OfferHasMimeType (offer, MimeRtf))
				return MimeRtf;
			return null;
		}

		string FindTextMimeType (WaylandDataOffer offer)
		{
			foreach (string mimeType in offer.MimeTypes) {
				if (String.Equals (mimeType, MimeTextUtf8, StringComparison.OrdinalIgnoreCase))
					return mimeType;
			}
			foreach (string mimeType in offer.MimeTypes) {
				if (IsTextMimeType (mimeType))
					return mimeType;
			}
			return null;
		}

		bool OfferHasMimeType (WaylandDataOffer offer, string expected)
		{
			foreach (string mimeType in offer.MimeTypes) {
				if (String.Equals (mimeType, expected, StringComparison.OrdinalIgnoreCase))
					return true;
			}
			return false;
		}

		bool IsTextMimeType (string mimeType)
		{
			return mimeType != null && mimeType.StartsWith (MimeTextPlain, StringComparison.OrdinalIgnoreCase);
		}

		byte [] ReceiveOfferData (WaylandDataOffer offer, string mimeType)
		{
			int [] pipeFds = new int [2];
			if (Syscall.pipe (pipeFds) != 0)
				UnixMarshal.ThrowExceptionForLastError ();

			try {
				RequireConnection ().SendRequestWithFd (offer.ObjectId, WaylandProtocol.WlDataOffer.Receive, delegate (WaylandRequestBuilder b) {
					b.WriteString (mimeType);
				}, pipeFds [1]);
				Syscall.close (pipeFds [1]);
				pipeFds [1] = -1;

				byte [] data = ReadFdToEnd (pipeFds [0]);
				pipeFds [0] = -1;
				return data;
			} finally {
				if (pipeFds [0] != -1)
					Syscall.close (pipeFds [0]);
				if (pipeFds [1] != -1)
					Syscall.close (pipeFds [1]);
			}
		}

		byte [] ReadFdToEnd (int fd)
		{
			using (UnixStream stream = new UnixStream (fd)) {
				MemoryStream memory = new MemoryStream ();
				byte [] buffer = new byte [4096];
				while (true) {
					Pollfd [] pollFds = new Pollfd [1];
					pollFds [0].fd = fd;
					pollFds [0].events = PollEvents.POLLIN | PollEvents.POLLHUP | PollEvents.POLLERR;
					int ready = Syscall.poll (pollFds, 1, 4000);
					if (ready < 0)
						UnixMarshal.ThrowExceptionForLastError ();
					if (ready == 0)
						throw new TimeoutException ("Timed out waiting for Wayland clipboard data.");

					int read = stream.Read (buffer, 0, buffer.Length);
					if (read == 0)
						break;
					memory.Write (buffer, 0, read);
				}
				return memory.ToArray ();
			}
		}

		string DecodeClipboardText (byte [] data)
		{
			int length = data.Length;
			if (length > 0 && data [length - 1] == 0)
				length--;
			return Encoding.UTF8.GetString (data, 0, length);
		}

		void HandleDataDeviceMessage (WaylandMessage message)
		{
			WaylandMessageReader reader = new WaylandMessageReader (message.Payload);
			switch (message.Opcode) {
			case WaylandProtocol.WlDataDevice.DataOffer: {
				uint offerId = reader.ReadUInt32 ();
				WaylandDataOffer offer = new WaylandDataOffer ();
				offer.ObjectId = offerId;
				dataOffers [offerId] = offer;
				break;
			}
			case WaylandProtocol.WlDataDevice.Selection:
				selectionOfferId = reader.ReadUInt32 ();
				if (selectionOfferId == 0 && clipboardSelection.SourceId == 0)
					clipboardSelection.OwnsSelection = false;
				break;
			}
		}

		void HandleDataOfferMessage (WaylandDataOffer offer, WaylandMessage message)
		{
			if (message.Opcode != WaylandProtocol.WlDataOffer.Offer)
				return;

			WaylandMessageReader reader = new WaylandMessageReader (message.Payload);
			string mimeType = reader.ReadString ();
			if (!offer.MimeTypes.Contains (mimeType))
				offer.MimeTypes.Add (mimeType);
		}

		void HandleDataSourceMessage (ClipboardSelection selection, WaylandMessage message)
		{
			WaylandMessageReader reader = new WaylandMessageReader (message.Payload);
			switch (message.Opcode) {
			case WaylandProtocol.WlDataSource.Target:
				reader.ReadString ();
				break;
			case WaylandProtocol.WlDataSource.Send: {
				string mimeType = reader.ReadString ();
				int fd;
				if (!RequireConnection ().TryTakeFd (out fd))
					throw new InvalidOperationException ("Wayland data_source.send did not include a file descriptor.");
				WriteSelectionData (selection, mimeType, fd);
				break;
			}
			case WaylandProtocol.WlDataSource.Cancelled:
				dataSources.Remove (message.ObjectId);
				if (selection.SourceId == message.ObjectId) {
					selection.SourceId = 0;
					selection.OwnsSelection = false;
				}
				RequireConnection ().SendRequest (message.ObjectId, WaylandProtocol.WlDataSource.Destroy, null);
				break;
			}
		}

		void WriteSelectionData (ClipboardSelection selection, string mimeType, int fd)
		{
			byte [] data = GetSelectionData (selection, mimeType);
			using (UnixStream stream = new UnixStream (fd)) {
				if (data != null && data.Length > 0)
					stream.Write (data, 0, data.Length);
			}
		}

		byte [] GetSelectionData (ClipboardSelection selection, string mimeType)
		{
			object obj;
			if (String.Equals (mimeType, MimeHtml, StringComparison.OrdinalIgnoreCase)) {
				if (TryGetStoredClipboardObject (selection, DataFormats.GetFormat (DataFormats.Html).Id, out obj) && obj is string)
					return Encoding.UTF8.GetBytes ((string) obj);
			} else if (String.Equals (mimeType, MimeRtf, StringComparison.OrdinalIgnoreCase)) {
				if (TryGetStoredClipboardObject (selection, DataFormats.GetFormat (DataFormats.Rtf).Id, out obj) && obj is string)
					return Encoding.UTF8.GetBytes ((string) obj);
			} else if (IsTextMimeType (mimeType)) {
				if (TryGetStoredClipboardObject (selection, ClipboardFormatUnicodeText, out obj) ||
				    TryGetStoredClipboardObject (selection, ClipboardFormatText, out obj) ||
				    TryGetStoredClipboardObject (selection, ClipboardFormatOemText, out obj) ||
				    TryGetStoredClipboardObject (selection, DataFormats.GetFormat (DataFormats.StringFormat).Id, out obj)) {
					if (obj is string)
						return Encoding.UTF8.GetBytes ((string) obj);
				}
			}

			return null;
		}

		bool TryGetStoredClipboardObject (ClipboardSelection selection, int id, out object obj)
		{
			return selection.Data.TryGetValue (id, out obj);
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

			if (message.ObjectId == dataDeviceId) {
				HandleDataDeviceMessage (message);
				return;
			}

			WaylandDataOffer offer;
			if (dataOffers.TryGetValue (message.ObjectId, out offer)) {
				HandleDataOfferMessage (offer, message);
				return;
			}

			ClipboardSelection dataSource;
			if (dataSources.TryGetValue (message.ObjectId, out dataSource)) {
				HandleDataSourceMessage (dataSource, message);
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
			bool debugKeyboard = Environment.GetEnvironmentVariable ("MONO_WAYLAND_DEBUG_KEYBOARD") == "1";

			if (debugKeyboard)
				Console.Error.WriteLine ("Wayland keyboard keymap event: format=" + format.ToString () +
					" size=" + size.ToString () + " pending_fds=" + connection.PendingFdCount.ToString ());

			if (format == WaylandProtocol.WlKeyboard.KeymapFormatNoKeymap) {
				ResetKeyboardLayout ("compositor sent no_keymap");
				return;
			}

			int fd;
			if (!connection.TryTakeFd (out fd)) {
				ResetKeyboardLayout ("keymap event had no received fd");
				return;
			}

			WaylandKeymap keymap;
			try {
				keymap = WaylandKeymap.ReadFromFd (format, fd, size);
			} catch {
				ResetKeyboardLayout ("failed to read keymap fd");
				return;
			}

			if (debugKeyboard)
				Console.Error.WriteLine ("Wayland keyboard keymap payload: bytes=" + keymap.Bytes.Length.ToString () +
					" text=" + keymap.Text.Length.ToString ());

			InstallKeyboardLayout (keymap);
		}

		void InstallKeyboardLayout (WaylandKeymap keymap)
		{
			string managedDiagnostic;
			string nativeDiagnostic = null;
			IWaylandKeyboardLayout layout = ManagedXkbKeyboardLayout.TryCreate (keymap, out managedDiagnostic);
			if (layout == null)
				layout = LibXkbCommonKeyboardLayout.TryCreate (keymap, out nativeDiagnostic);
			bool physicalFallback = layout == null;
			if (physicalFallback)
				layout = new PhysicalUsKeyboardLayout ();

			keyboardLayout.Dispose ();
			keyboardLayout = layout;
			keyboardLayout.SetModifiers (keyboardModsDepressed, keyboardModsLatched, keyboardModsLocked, keyboardGroup);
			modifierKeys = keyboardLayout.ModifierKeys;
			keyText.Clear ();
			keySysState.Clear ();
			if (Environment.GetEnvironmentVariable ("MONO_WAYLAND_DEBUG_KEYBOARD") == "1") {
				Console.Error.WriteLine ("Wayland keyboard layout: " + keyboardLayout.GetType ().Name);
				if (!String.IsNullOrEmpty (managedDiagnostic))
					Console.Error.WriteLine ("Wayland keyboard managed XKB: " + managedDiagnostic);
				if (!String.IsNullOrEmpty (nativeDiagnostic))
					Console.Error.WriteLine ("Wayland keyboard libxkbcommon: " + nativeDiagnostic);
				if (physicalFallback)
					Console.Error.WriteLine ("Wayland keyboard fallback: PhysicalUsKeyboardLayout");
			}
		}

		void ResetKeyboardLayout ()
		{
			ResetKeyboardLayout (null);
		}

		void ResetKeyboardLayout (string reason)
		{
			keyboardLayout.Dispose ();
			keyboardLayout = new PhysicalUsKeyboardLayout ();
			modifierKeys = Keys.None;
			keyboardModsDepressed = 0;
			keyboardModsLatched = 0;
			keyboardModsLocked = 0;
			keyboardGroup = 0;
			keyText.Clear ();
			keySysState.Clear ();
			if (!String.IsNullOrEmpty (reason) && Environment.GetEnvironmentVariable ("MONO_WAYLAND_DEBUG_KEYBOARD") == "1") {
				Console.Error.WriteLine ("Wayland keyboard layout: PhysicalUsKeyboardLayout");
				Console.Error.WriteLine ("Wayland keyboard fallback: " + reason);
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
			bool storedSysKey = false;
			bool hasStoredSysKey = !pressed && keySysState.TryGetValue (evdevKey, out storedSysKey);

			if (pressed)
				evdevKeysDown.Add (evdevKey);
			else {
				evdevKeysDown.Remove (evdevKey);
				keySysState.Remove (evdevKey);
			}

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

			Keys shortcutModifiers = result.HasShortcutModifiers ? result.ShortcutModifiers : (pressed ? modifierKeys : oldModifiers);
			bool sysKey;
			if (pressed) {
				// WM_SYSKEYUP must match the key-down classification.  XKB may
				// consume Alt-like modifiers for text selection, and release
				// events arrive after the modifier state has changed.
				sysKey = IsSystemKeyModifierState (shortcutModifiers);
				keySysState [evdevKey] = sysKey;
			} else if (hasStoredSysKey)
				sysKey = storedSysKey;
			else
				sysKey = IsSystemKeyModifierState (shortcutModifiers);
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

		static bool IsSystemKeyModifierState (Keys modifiers)
		{
			return (modifiers & Keys.Alt) != 0 && (modifiers & Keys.Control) == 0;
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
			if (keysym >= XkbKeysymF1 && keysym <= XkbKeysymF12)
				return (Keys) ((int) Keys.F1 + (int) (keysym - XkbKeysymF1));
			if (keysym >= XkbKeysymKp0 && keysym <= XkbKeysymKp9)
				return (Keys) ((int) Keys.NumPad0 + (int) (keysym - XkbKeysymKp0));

			switch (keysym) {
			case XkbKeysymEscape: return Keys.Escape;
			case XkbKeysymBackSpace: return Keys.Back;
			case XkbKeysymTab: return Keys.Tab;
			case XkbKeysymReturn: return Keys.Return;
			case XkbKeysymDelete: return Keys.Delete;
			case XkbKeysymInsert: return Keys.Insert;
			case XkbKeysymHome: return Keys.Home;
			case XkbKeysymEnd: return Keys.End;
			case XkbKeysymPageUp: return Keys.PageUp;
			case XkbKeysymPageDown: return Keys.PageDown;
			case XkbKeysymLeft: return Keys.Left;
			case XkbKeysymUp: return Keys.Up;
			case XkbKeysymRight: return Keys.Right;
			case XkbKeysymDown: return Keys.Down;
			case XkbKeysymShiftL:
			case XkbKeysymShiftR: return Keys.ShiftKey;
			case XkbKeysymControlL:
			case XkbKeysymControlR: return Keys.ControlKey;
			case XkbKeysymAltL:
			case XkbKeysymAltR: return Keys.Menu;
			case XkbKeysymSuperL: return Keys.LWin;
			case XkbKeysymSuperR: return Keys.RWin;
			case XkbKeysymCapsLock: return Keys.CapsLock;
			case XkbKeysymNumLock: return Keys.NumLock;
			case XkbKeysymScrollLock: return Keys.Scroll;
			case XkbKeysymKpEnter: return Keys.Return;
			case XkbKeysymKpMultiply: return Keys.Multiply;
			case XkbKeysymKpAdd: return Keys.Add;
			case XkbKeysymKpSubtract: return Keys.Subtract;
			case XkbKeysymKpDecimal: return Keys.Decimal;
			case XkbKeysymKpDivide: return Keys.Divide;
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
			case EvdevKeyLeftControl: return Keys.ControlKey;
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
			case EvdevKeyLeftShift: return Keys.ShiftKey;
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
			case EvdevKeyRightShift: return Keys.ShiftKey;
			case 55: return Keys.Multiply;
			case EvdevKeyLeftAlt: return Keys.Menu;
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
			case EvdevKeyRightControl: return Keys.ControlKey;
			case 98: return Keys.Divide;
			case EvdevKeyRightAlt: return Keys.RMenu;
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

			// Mono's SetZOrder(handle, after) has the Win32/X11 meaning used by
			// the other backends: handle is placed below after.  Wayland names
			// that operation directly as wl_subsurface.place_below.  Bottom is
			// the lowest visible child, just above the parent surface; top and
			// after==NULL are placed above the current topmost sibling.
			uint targetSurface = parent.SurfaceId;
			ushort opcode = WaylandProtocol.WlSubsurface.PlaceAbove;
			WaylandWindow sibling;
			if (!top && !bottom && afterHWnd != IntPtr.Zero && windows.TryGetValue (afterHWnd, out sibling) && sibling.Hwnd.Parent == window.Hwnd.Parent && sibling.SurfaceId != 0) {
				targetSurface = sibling.SurfaceId;
				opcode = WaylandProtocol.WlSubsurface.PlaceBelow;
			} else if (top || (!bottom && afterHWnd == IntPtr.Zero)) {
				foreach (IntPtr handle in zOrder) {
					if (handle == window.Hwnd.Handle)
						continue;
					if (windows.TryGetValue (handle, out sibling) && sibling.Hwnd.Parent == window.Hwnd.Parent && sibling.SurfaceId != 0)
						targetSurface = sibling.SurfaceId;
				}
			}

			WaylandConnection liveConnection = RequireConnection ();
			liveConnection.SendRequest (window.SubsurfaceId, opcode, delegate (WaylandRequestBuilder b) {
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
