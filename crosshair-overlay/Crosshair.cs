// Crosshair Overlay for Windows
//
// A single-file C# 5 application that draws a static crosshair at the center
// of a monitor using a topmost, click-through layered window. It never touches
// any other process: no injection, no hooks, no memory reads, no input
// simulation. It is the same mechanism a monitor's built-in OSD crosshair or
// the Discord/NVIDIA overlays use.
//
// Build with build.cmd (uses the in-box .NET Framework 4.x csc.exe).
// See README.md for hotkeys, tray menu, config keys and troubleshooting.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Crosshair Overlay")]
[assembly: AssemblyProduct("Crosshair Overlay")]
[assembly: AssemblyDescription("Static click-through crosshair overlay for Windows")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace CrosshairOverlay
{
    // ------------------------------------------------------------------
    // Crosshair styles
    // ------------------------------------------------------------------

    public enum CrosshairStyle
    {
        Cross = 0,
        CrossDot = 1,
        Dot = 2,
        Circle = 3,
        CircleDot = 4,
        X = 5
    }

    // ------------------------------------------------------------------
    // Win32 interop
    // ------------------------------------------------------------------

    internal static class Native
    {
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TOPMOST = 0x00000008;

        public const int WM_MOUSEACTIVATE = 0x0021;
        public const int WM_NCHITTEST = 0x0084;
        public const int WM_HOTKEY = 0x0312;

        public const int MA_NOACTIVATE = 3;
        public const int HTTRANSPARENT = -1;

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;

        public const byte AC_SRC_OVER = 0x00;
        public const byte AC_SRC_ALPHA = 0x01;
        public const int ULW_ALPHA = 0x00000002;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
            public POINT(int x, int y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE
        {
            public int Width;
            public int Height;
            public SIZE(int w, int h) { Width = w; Height = h; }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetProcessDPIAware();
    }

    // ------------------------------------------------------------------
    // Hotkey specification: "[Ctrl+][Alt+][Shift+][Win+]Key"
    // ------------------------------------------------------------------

    public class HotkeySpec
    {
        public uint Modifiers;
        public Keys Key;
        public bool Enabled;
        public string Text;

        public static HotkeySpec Disabled()
        {
            HotkeySpec h = new HotkeySpec();
            h.Modifiers = 0;
            h.Key = Keys.None;
            h.Enabled = false;
            h.Text = "";
            return h;
        }

        // Returns null when the string is malformed so the caller can fall
        // back to a default. An empty string yields a disabled hotkey.
        public static HotkeySpec Parse(string text)
        {
            if (text == null)
            {
                return null;
            }
            string trimmed = text.Trim();
            if (trimmed.Length == 0)
            {
                return Disabled();
            }

            string[] parts = trimmed.Split('+');
            uint mods = 0;
            Keys key = Keys.None;
            bool haveKey = false;

            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length == 0)
                {
                    return null;
                }
                string lower = p.ToLowerInvariant();
                if (lower == "ctrl" || lower == "control")
                {
                    mods |= Native.MOD_CONTROL;
                }
                else if (lower == "alt")
                {
                    mods |= Native.MOD_ALT;
                }
                else if (lower == "shift")
                {
                    mods |= Native.MOD_SHIFT;
                }
                else if (lower == "win" || lower == "windows")
                {
                    mods |= Native.MOD_WIN;
                }
                else
                {
                    if (haveKey)
                    {
                        return null; // two non-modifier tokens
                    }
                    string keyName = p;
                    if (keyName.Length == 1 && char.IsDigit(keyName[0]))
                    {
                        keyName = "D" + keyName;
                    }
                    Keys parsed;
                    if (!TryParseKey(keyName, out parsed))
                    {
                        return null;
                    }
                    key = parsed & Keys.KeyCode;
                    if (key == Keys.None || key == Keys.ControlKey || key == Keys.ShiftKey || key == Keys.Menu)
                    {
                        return null;
                    }
                    haveKey = true;
                }
            }

            if (!haveKey)
            {
                return null;
            }

            HotkeySpec h = new HotkeySpec();
            h.Modifiers = mods;
            h.Key = key;
            h.Enabled = true;
            h.Text = BuildText(mods, key);
            return h;
        }

        private static bool TryParseKey(string name, out Keys key)
        {
            key = Keys.None;
            try
            {
                key = (Keys)Enum.Parse(typeof(Keys), name, true);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string BuildText(uint mods, Keys key)
        {
            StringBuilder sb = new StringBuilder();
            if ((mods & Native.MOD_CONTROL) != 0) sb.Append("Ctrl+");
            if ((mods & Native.MOD_ALT) != 0) sb.Append("Alt+");
            if ((mods & Native.MOD_SHIFT) != 0) sb.Append("Shift+");
            if ((mods & Native.MOD_WIN) != 0) sb.Append("Win+");
            string keyName = key.ToString();
            if (keyName.Length == 2 && keyName[0] == 'D' && char.IsDigit(keyName[1]))
            {
                keyName = keyName.Substring(1);
            }
            sb.Append(keyName);
            return sb.ToString();
        }

        public override string ToString()
        {
            return Text;
        }
    }

    // ------------------------------------------------------------------
    // Configuration (plain key=value file)
    // ------------------------------------------------------------------

    public class Config
    {
        public const string FileName = "crosshair.ini";

        public string FilePath;

        public CrosshairStyle Style = CrosshairStyle.Cross;
        public Color Color = Color.FromArgb(0x00, 0xFF, 0x00);
        public int Opacity = 255;
        public int Size = 12;
        public int Thickness = 2;
        public int Gap = 4;
        public bool Outline = true;
        public Color OutlineColor = Color.FromArgb(0x00, 0x00, 0x00);
        public int DotSize = 3;
        public int OffsetX = 0;
        public int OffsetY = 0;
        public string Monitor = "primary";
        public bool Visible = true;
        public HotkeySpec HotkeyToggle = HotkeySpec.Parse("Ctrl+Alt+X");
        public HotkeySpec HotkeyCycleColor = HotkeySpec.Parse("Ctrl+Alt+C");
        public HotkeySpec HotkeyCycleStyle = HotkeySpec.Parse("Ctrl+Alt+V");

        // Preset colors used by the tray menu and the cycle-color hotkey.
        public static readonly string[] PresetNames = new string[]
        {
            "Green", "Red", "Cyan", "Magenta", "Yellow", "White", "Orange", "Pink"
        };
        public static readonly Color[] PresetColors = new Color[]
        {
            Color.FromArgb(0x00, 0xFF, 0x00),
            Color.FromArgb(0xFF, 0x00, 0x00),
            Color.FromArgb(0x00, 0xFF, 0xFF),
            Color.FromArgb(0xFF, 0x00, 0xFF),
            Color.FromArgb(0xFF, 0xFF, 0x00),
            Color.FromArgb(0xFF, 0xFF, 0xFF),
            Color.FromArgb(0xFF, 0x80, 0x00),
            Color.FromArgb(0xFF, 0x69, 0xB4)
        };

        // Chooses crosshair.ini next to the exe when that file already exists
        // and can be read, or when the exe folder is writable; otherwise
        // %APPDATA%\CrosshairOverlay\crosshair.ini. The ini file itself is
        // never created or opened for writing here, so a read-only or briefly
        // locked local ini does not cause a switch to %APPDATA%.
        public static string ResolvePath()
        {
            string exeDir = null;
            try
            {
                exeDir = Path.GetDirectoryName(Application.ExecutablePath);
            }
            catch (Exception)
            {
                exeDir = null;
            }

            if (!string.IsNullOrEmpty(exeDir))
            {
                string local = Path.Combine(exeDir, FileName);

                // An existing, readable local ini always wins. Save() may still
                // fail later (read-only file); it returns false without throwing.
                bool exists = false;
                try
                {
                    exists = File.Exists(local);
                }
                catch (Exception)
                {
                    exists = false;
                }
                if (exists)
                {
                    try
                    {
                        using (FileStream fs = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        {
                            // Readable: use it.
                        }
                        return local;
                    }
                    catch (Exception)
                    {
                        // Unreadable right now; decide by folder writability below.
                    }
                }

                // Probe the folder, not the ini: create and delete a throwaway
                // file. Success means the folder is writable and the local
                // path is used (Save() creates the ini later).
                if (IsFolderWritable(exeDir))
                {
                    return local;
                }
            }

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(appData, "CrosshairOverlay");
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (Exception)
            {
                // If even this fails, Save() will report failure quietly and
                // the program still runs with in-memory settings.
            }
            return Path.Combine(dir, FileName);
        }

        private static bool IsFolderWritable(string dir)
        {
            string probe = null;
            try
            {
                probe = Path.Combine(dir, Path.GetRandomFileName());
                using (FileStream fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    // Creating the file proves the folder is writable.
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (probe != null)
                {
                    try
                    {
                        if (File.Exists(probe))
                        {
                            File.Delete(probe);
                        }
                    }
                    catch (Exception)
                    {
                        // Leaving a zero-byte random file behind is harmless.
                    }
                }
            }
        }

        public static Config Load(string path)
        {
            Config c = new Config();
            c.FilePath = path;

            string[] lines = null;
            try
            {
                if (File.Exists(path))
                {
                    lines = File.ReadAllLines(path);
                }
            }
            catch (Exception)
            {
                lines = null;
            }

            if (lines == null)
            {
                return c;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
                {
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                c.ApplyKey(key, value);
            }

            return c;
        }

        private void ApplyKey(string key, string value)
        {
            string k = key.ToLowerInvariant();
            switch (k)
            {
                case "style":
                    {
                        CrosshairStyle s;
                        if (TryParseStyle(value, out s)) Style = s;
                        break;
                    }
                case "color":
                    {
                        Color col;
                        if (TryParseColor(value, out col)) Color = col;
                        break;
                    }
                case "opacity":
                    Opacity = ParseInt(value, Opacity, 0, 255);
                    break;
                case "size":
                    Size = ParseInt(value, Size, 1, 500);
                    break;
                case "thickness":
                    Thickness = ParseInt(value, Thickness, 1, 50);
                    break;
                case "gap":
                    Gap = ParseInt(value, Gap, 0, 500);
                    break;
                case "outline":
                    Outline = ParseBool(value, Outline);
                    break;
                case "outlinecolor":
                    {
                        Color col;
                        if (TryParseColor(value, out col)) OutlineColor = col;
                        break;
                    }
                case "dotsize":
                    DotSize = ParseInt(value, DotSize, 1, 100);
                    break;
                case "offsetx":
                    OffsetX = ParseInt(value, OffsetX, -10000, 10000);
                    break;
                case "offsety":
                    OffsetY = ParseInt(value, OffsetY, -10000, 10000);
                    break;
                case "monitor":
                    {
                        string v = value.Trim().ToLowerInvariant();
                        int idx;
                        if (v == "primary")
                        {
                            Monitor = "primary";
                        }
                        else if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out idx) && idx >= 0)
                        {
                            Monitor = idx.ToString(CultureInfo.InvariantCulture);
                        }
                        break;
                    }
                case "visible":
                    Visible = ParseBool(value, Visible);
                    break;
                case "hotkeytoggle":
                    {
                        HotkeySpec h = HotkeySpec.Parse(value);
                        if (h != null) HotkeyToggle = h;
                        break;
                    }
                case "hotkeycyclecolor":
                    {
                        HotkeySpec h = HotkeySpec.Parse(value);
                        if (h != null) HotkeyCycleColor = h;
                        break;
                    }
                case "hotkeycyclestyle":
                    {
                        HotkeySpec h = HotkeySpec.Parse(value);
                        if (h != null) HotkeyCycleStyle = h;
                        break;
                    }
                default:
                    // Unknown keys are ignored on purpose.
                    break;
            }
        }

        public static bool TryParseStyle(string value, out CrosshairStyle style)
        {
            style = CrosshairStyle.Cross;
            if (value == null)
            {
                return false;
            }
            string v = value.Trim().Replace(" ", "").Replace("+", "");
            try
            {
                style = (CrosshairStyle)Enum.Parse(typeof(CrosshairStyle), v, true);
                return Enum.IsDefined(typeof(CrosshairStyle), style);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool TryParseColor(string value, out Color color)
        {
            color = Color.Black;
            if (value == null)
            {
                return false;
            }
            string v = value.Trim();
            if (v.StartsWith("#"))
            {
                v = v.Substring(1);
            }
            if (v.Length != 6)
            {
                return false;
            }
            int rgb;
            if (!int.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb))
            {
                return false;
            }
            color = Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
            return true;
        }

        public static string ColorToHex(Color c)
        {
            return string.Format("{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
        }

        private static int ParseInt(string value, int fallback, int min, int max)
        {
            int n;
            if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
            {
                return fallback;
            }
            if (n < min) n = min;
            if (n > max) n = max;
            return n;
        }

        private static bool ParseBool(string value, bool fallback)
        {
            string v = value.Trim().ToLowerInvariant();
            if (v == "1" || v == "true" || v == "yes" || v == "on") return true;
            if (v == "0" || v == "false" || v == "no" || v == "off") return false;
            return fallback;
        }

        public bool Save()
        {
            if (string.IsNullOrEmpty(FilePath))
            {
                return false;
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# Crosshair Overlay configuration");
            sb.AppendLine("# One key=value per line. Lines starting with # are comments.");
            sb.AppendLine("# Malformed values fall back to their defaults. Unknown keys are ignored.");
            sb.AppendLine("# Changes made from the tray menu or hotkeys are written here immediately.");
            sb.AppendLine("# Use the tray menu's Reload config item after editing this file by hand.");
            sb.AppendLine();
            sb.AppendLine("# Style: Cross, CrossDot, Dot, Circle, CircleDot, X");
            sb.AppendLine("Style=" + Style.ToString());
            sb.AppendLine("# Color as hex RRGGBB");
            sb.AppendLine("Color=" + ColorToHex(Color));
            sb.AppendLine("# Opacity 0-255 applied to the whole crosshair");
            sb.AppendLine("Opacity=" + Opacity.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("# Size: half-length of each arm in pixels (also the circle radius)");
            sb.AppendLine("Size=" + Size.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("# Thickness: line thickness in pixels");
            sb.AppendLine("Thickness=" + Thickness.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("# Gap: empty pixels between the center and the start of each arm");
            sb.AppendLine("Gap=" + Gap.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("# Outline: 1 draws a 1px outline around the shapes for contrast, 0 disables it");
            sb.AppendLine("Outline=" + (Outline ? "1" : "0"));
            sb.AppendLine("# OutlineColor as hex RRGGBB");
            sb.AppendLine("OutlineColor=" + ColorToHex(OutlineColor));
            sb.AppendLine("# DotSize: diameter of the center dot in pixels (CrossDot, Dot, CircleDot)");
            sb.AppendLine("DotSize=" + DotSize.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("# OffsetX / OffsetY: pixels to shift right / down (negative = left / up)");
            sb.AppendLine("OffsetX=" + OffsetX.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("OffsetY=" + OffsetY.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("# Monitor: primary, or a zero-based monitor index");
            sb.AppendLine("Monitor=" + Monitor);
            sb.AppendLine("# Visible: 1 shows the crosshair at startup, 0 starts hidden");
            sb.AppendLine("Visible=" + (Visible ? "1" : "0"));
            sb.AppendLine("# Hotkeys: [Ctrl+][Alt+][Shift+][Win+]Key, for example Ctrl+Alt+X or Shift+F6. Empty disables the hotkey.");
            sb.AppendLine("HotkeyToggle=" + HotkeyToggle.Text);
            sb.AppendLine("HotkeyCycleColor=" + HotkeyCycleColor.Text);
            sb.AppendLine("HotkeyCycleStyle=" + HotkeyCycleStyle.Text);

            try
            {
                string dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public Screen ResolveScreen()
        {
            Screen[] all = Screen.AllScreens;
            int idx;
            if (Monitor != null && int.TryParse(Monitor, NumberStyles.Integer, CultureInfo.InvariantCulture, out idx))
            {
                if (idx >= 0 && idx < all.Length)
                {
                    return all[idx];
                }
            }
            return Screen.PrimaryScreen;
        }
    }

    // ------------------------------------------------------------------
    // Rendering
    // ------------------------------------------------------------------

    internal static class Renderer
    {
        // Where the anti-aliased shapes (circle, X, dot) put the center pixel's
        // middle, relative to the integer center pixel (cx, cy).
        //
        // Measured against GDI+ in .NET Framework 4.8 on Windows 11 with
        // verify\RenderTest.cs and a sub-pixel sweep:
        //
        //  * With PixelOffsetMode.HighQuality (= Half) integer coordinates
        //    address pixel CORNERS, so the middle of pixel cx is at cx + 0.5.
        //    A 1 px line at y = cy + 0.5 lands on exactly one pixel row; at
        //    y = cy it straddles two rows at 50% each. Dropping the 0.5 shifts
        //    every shape half a pixel up and left of the cross arms.
        //
        //  * GDI+'s anti-aliasing coverage sampling is itself biased: with the
        //    shape centered at exactly cx + 0.5 the alpha-weighted centroid
        //    lands about 0.05-0.11 px right and 0.10-0.20 px below the center
        //    pixel for every size and thickness, and the default 3 px dot comes
        //    out with a heavier bottom-right corner. Moving the center by
        //    -3/32 px horizontally and -5/32 px vertically cancels this: the
        //    dot and the X then rasterize exactly mirror-symmetric, and the
        //    circle's centroid error drops below 0.05 px (ellipses are
        //    flattened to curves by GDI+ and never come out exactly symmetric).
        //    Any value in [-3/32, -1/16] x [-5/32, -1/8] gives the same result.
        //
        // The cross arms are integer rectangles and do not use these.
        private const float AaCenterX = 0.5f - 0.09375f;
        private const float AaCenterY = 0.5f - 0.15625f;

        // Renders the crosshair into a premultiplied-alpha bitmap. The bitmap
        // is square with an odd side length so that the exact center pixel is
        // at (extent, extent).
        //
        // Both passes are drawn fully opaque. Config.Opacity is NOT baked into
        // the pixels: it is applied once for the whole surface at push time via
        // BLENDFUNCTION.SourceConstantAlpha (see OverlayForm.PushToScreen).
        // Baking it into both passes made the translucent main pass composite
        // over the translucent outline pass, so the body ended up more opaque
        // than requested with the outline color bleeding through.
        public static Bitmap Render(Config c, out int extent)
        {
            int outline = c.Outline ? 1 : 0;
            int reach = Math.Max(c.Gap + c.Size, c.Size + c.Thickness);
            reach = Math.Max(reach, c.DotSize);
            extent = reach + c.Thickness + outline + 2;
            int side = extent * 2 + 1;

            Bitmap bmp = new Bitmap(side, side, PixelFormat.Format32bppPArgb);
            try
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    Color main = Color.FromArgb(255, c.Color.R, c.Color.G, c.Color.B);
                    Color edge = Color.FromArgb(255, c.OutlineColor.R, c.OutlineColor.G, c.OutlineColor.B);

                    if (c.Outline)
                    {
                        DrawShapes(g, c, extent, extent, edge, 1);
                    }
                    DrawShapes(g, c, extent, extent, main, 0);
                }
            }
            catch
            {
                bmp.Dispose();
                throw;
            }
            return bmp;
        }

        // Draws every shape of the current style in one color. grow > 0
        // enlarges each shape by that many pixels on every side, which is how
        // the outline pass works.
        private static void DrawShapes(Graphics g, Config c, int cx, int cy, Color color, int grow)
        {
            bool cross = c.Style == CrosshairStyle.Cross || c.Style == CrosshairStyle.CrossDot;
            bool dot = c.Style == CrosshairStyle.CrossDot || c.Style == CrosshairStyle.Dot || c.Style == CrosshairStyle.CircleDot;
            bool circle = c.Style == CrosshairStyle.Circle || c.Style == CrosshairStyle.CircleDot;
            bool xShape = c.Style == CrosshairStyle.X;

            if (cross)
            {
                DrawCross(g, cx, cy, c.Size, c.Thickness, c.Gap, color, grow);
            }

            // Anti-aliased shapes are centered on the middle of the center
            // pixel; see AaCenterX / AaCenterY above for why it is not (cx, cy).
            float acx = cx + AaCenterX;
            float acy = cy + AaCenterY;

            if (circle)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float penWidth = c.Thickness + 2 * grow;
                float r = c.Size;
                using (Pen pen = new Pen(color, penWidth))
                {
                    g.DrawEllipse(pen, acx - r, acy - r, 2 * r, 2 * r);
                }
            }

            if (xShape)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float penWidth = c.Thickness + 2 * grow;
                float inner = c.Gap;
                float outer = c.Gap + c.Size;
                if (grow > 0)
                {
                    inner = Math.Max(0f, inner - grow);
                    outer = outer + grow;
                }
                float k = 0.70710678f;
                float ox = acx;
                float oy = acy;
                using (Pen pen = new Pen(color, penWidth))
                {
                    pen.StartCap = LineCap.Flat;
                    pen.EndCap = LineCap.Flat;
                    int[] sx = new int[] { 1, -1, 1, -1 };
                    int[] sy = new int[] { 1, 1, -1, -1 };
                    for (int i = 0; i < 4; i++)
                    {
                        float x1 = ox + sx[i] * inner * k;
                        float y1 = oy + sy[i] * inner * k;
                        float x2 = ox + sx[i] * outer * k;
                        float y2 = oy + sy[i] * outer * k;
                        g.DrawLine(pen, x1, y1, x2, y2);
                    }
                }
            }

            if (dot)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float d = c.DotSize + 2 * grow;
                using (SolidBrush brush = new SolidBrush(color))
                {
                    g.FillEllipse(brush, acx - d / 2f, acy - d / 2f, d, d);
                }
            }
        }

        // Cross arms are integer-aligned filled rectangles so they stay
        // pixel-crisp. The center pixel is (cx, cy) and every arm contains it
        // in its width. For odd thickness the arm is centered exactly on that
        // pixel. For even thickness a hard-edged arm cannot be centered on a
        // single pixel, so it straddles it the same way on all four arms:
        // thickness / 2 pixels on the left/top side of the center pixel and
        // (thickness - 1) / 2 on the right/bottom side. The anti-aliased dot is
        // centered on the same pixel, so the two always share a center.
        public static void DrawCross(Graphics g, int cx, int cy, int size, int thickness, int gap, Color color, int grow)
        {
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.None;

            int half = thickness / 2;                   // pixels left/above the center pixel; (thickness - 1) / 2 fall right/below
            int armLen = size + (gap == 0 ? 1 : 0);
            int nearStart = gap == 0 ? 0 : gap + 1;   // first pixel of the down/right arm, relative to center
            int farStart = gap + size;                  // distance from center to the far end of the up/left arm

            using (SolidBrush brush = new SolidBrush(color))
            {
                // Up
                g.FillRectangle(brush, new Rectangle(cx - half - grow, cy - farStart - grow, thickness + 2 * grow, armLen + 2 * grow));
                // Down
                g.FillRectangle(brush, new Rectangle(cx - half - grow, cy + nearStart - grow, thickness + 2 * grow, armLen + 2 * grow));
                // Left
                g.FillRectangle(brush, new Rectangle(cx - farStart - grow, cy - half - grow, armLen + 2 * grow, thickness + 2 * grow));
                // Right
                g.FillRectangle(brush, new Rectangle(cx + nearStart - grow, cy - half - grow, armLen + 2 * grow, thickness + 2 * grow));
            }
        }

        // Builds a 16x16 tray icon showing a mini crosshair in the given color.
        // The caller owns the returned HICON and must call DestroyIcon on it
        // after the Icon object is no longer in use.
        public static Icon CreateTrayIcon(Color color, out IntPtr hIcon)
        {
            using (Bitmap bmp = new Bitmap(16, 16, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    Color edge = Color.FromArgb(220, 0, 0, 0);
                    Color main = Color.FromArgb(255, color.R, color.G, color.B);
                    DrawCross(g, 8, 8, 5, 2, 1, edge, 1);
                    DrawCross(g, 8, 8, 5, 2, 1, main, 0);
                }
                hIcon = bmp.GetHicon();
                return Icon.FromHandle(hIcon);
            }
        }
    }

    // ------------------------------------------------------------------
    // Small numeric prompt used by the "Custom..." size entry
    // ------------------------------------------------------------------

    internal static class Prompt
    {
        public static bool AskInt(string title, string label, int min, int max, int current, out int result)
        {
            result = current;
            using (Form f = new Form())
            {
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterScreen;
                f.MinimizeBox = false;
                f.MaximizeBox = false;
                f.ShowInTaskbar = false;
                f.TopMost = true;
                f.ClientSize = new Size(280, 104);

                Label lbl = new Label();
                lbl.Text = label;
                lbl.SetBounds(12, 12, 256, 20);

                NumericUpDown num = new NumericUpDown();
                num.Minimum = min;
                num.Maximum = max;
                int clamped = current;
                if (clamped < min) clamped = min;
                if (clamped > max) clamped = max;
                num.Value = clamped;
                num.SetBounds(12, 36, 256, 24);

                Button ok = new Button();
                ok.Text = "OK";
                ok.DialogResult = DialogResult.OK;
                ok.SetBounds(112, 70, 75, 26);

                Button cancel = new Button();
                cancel.Text = "Cancel";
                cancel.DialogResult = DialogResult.Cancel;
                cancel.SetBounds(193, 70, 75, 26);

                f.Controls.Add(lbl);
                f.Controls.Add(num);
                f.Controls.Add(ok);
                f.Controls.Add(cancel);
                f.AcceptButton = ok;
                f.CancelButton = cancel;

                if (f.ShowDialog() == DialogResult.OK)
                {
                    result = (int)num.Value;
                    return true;
                }
            }
            return false;
        }
    }

    // ------------------------------------------------------------------
    // The overlay window
    // ------------------------------------------------------------------

    public class OverlayForm : Form
    {
        private const int HOTKEY_ID_TOGGLE = 1;
        private const int HOTKEY_ID_CYCLE_COLOR = 2;
        private const int HOTKEY_ID_CYCLE_STYLE = 3;

        private const string RUN_KEY_PATH = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RUN_VALUE_NAME = "CrosshairOverlay";

        private Config config;
        private bool allowVisible;
        private bool hotkeysRegistered;

        private System.Windows.Forms.Timer topmostTimer;
        private NotifyIcon trayIcon;
        private IntPtr trayIconHandle = IntPtr.Zero;
        private Icon trayIconObject;

        private ContextMenuStrip menu;
        private ToolStripMenuItem miToggle;
        private ToolStripMenuItem miStyle;
        private ToolStripMenuItem miColor;
        private ToolStripMenuItem miSize;
        private ToolStripMenuItem miThickness;
        private ToolStripMenuItem miGap;
        private ToolStripMenuItem miOpacity;
        private ToolStripMenuItem miOutline;
        private ToolStripMenuItem miMonitor;
        private ToolStripMenuItem miNudge;
        private ToolStripMenuItem miStartup;

        private static readonly int[] SizePresets = new int[] { 8, 12, 18, 26 };
        private static readonly string[] SizeNames = new string[] { "Small (8)", "Medium (12)", "Large (18)", "Extra Large (26)" };
        private static readonly int[] ThicknessPresets = new int[] { 1, 2, 3, 4 };
        private static readonly int[] GapPresets = new int[] { 0, 2, 4, 6, 8, 12 };
        private static readonly int[] OpacityPercents = new int[] { 100, 80, 60, 40 };
        private static readonly string[] StyleNames = new string[] { "Cross", "Cross + Dot", "Dot", "Circle", "Circle + Dot", "X" };

        public OverlayForm(Config cfg)
        {
            config = cfg;
            allowVisible = cfg.Visible;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "CrosshairOverlay";
            AutoScaleMode = AutoScaleMode.None;
            MinimizeBox = false;
            MaximizeBox = false;
            ControlBox = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
            SetStyle(ControlStyles.Selectable, false);

            topmostTimer = new System.Windows.Forms.Timer();
            topmostTimer.Interval = 1000;
            topmostTimer.Tick += OnTopmostTick;

            BuildTrayMenu();
            BuildTrayIcon();

            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        }

        // --- window styles and activation -----------------------------------

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW
                    | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        // Application.Run forces Visible = true on the main form. This lets the
        // window start hidden when the config says so while still creating the
        // handle so hotkeys and the tray icon work.
        protected override void SetVisibleCore(bool value)
        {
            if (!allowVisible)
            {
                value = false;
                if (!IsHandleCreated)
                {
                    CreateHandle();
                }
            }
            base.SetVisibleCore(value);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // The surface comes from UpdateLayeredWindow; nothing to paint.
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // Intentionally empty, see OnPaintBackground.
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterHotkeys();
            Render();
            if (allowVisible)
            {
                topmostTimer.Start();
            }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            UnregisterHotkeys();
            base.OnHandleDestroyed(e);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible)
            {
                AssertTopmost();
                topmostTimer.Start();
            }
            else
            {
                topmostTimer.Stop();
            }
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case Native.WM_MOUSEACTIVATE:
                    m.Result = new IntPtr(Native.MA_NOACTIVATE);
                    return;
                case Native.WM_NCHITTEST:
                    m.Result = new IntPtr(Native.HTTRANSPARENT);
                    return;
                case Native.WM_HOTKEY:
                    {
                        int id = m.WParam.ToInt32();
                        if (id == HOTKEY_ID_TOGGLE)
                        {
                            ToggleVisible();
                        }
                        else if (id == HOTKEY_ID_CYCLE_COLOR)
                        {
                            CycleColor();
                        }
                        else if (id == HOTKEY_ID_CYCLE_STYLE)
                        {
                            CycleStyle();
                        }
                        m.Result = IntPtr.Zero;
                        return;
                    }
            }
            base.WndProc(ref m);
        }

        // --- rendering --------------------------------------------------------

        private void Render()
        {
            if (!IsHandleCreated)
            {
                return;
            }

            int extent;
            Bitmap bmp = null;
            try
            {
                bmp = Renderer.Render(config, out extent);
                Screen screen = config.ResolveScreen();
                Rectangle b = screen.Bounds;
                int centerX = b.Left + b.Width / 2 + config.OffsetX;
                int centerY = b.Top + b.Height / 2 + config.OffsetY;
                int left = centerX - extent;
                int top = centerY - extent;
                int side = bmp.Width;

                Bounds = new Rectangle(left, top, side, side);
                PushToScreen(bmp, left, top, side, side);
            }
            finally
            {
                if (bmp != null)
                {
                    bmp.Dispose();
                }
            }
        }

        // Copies the bitmap into the layered window. Every GDI handle created
        // here is released in the finally block so the object count stays flat.
        private void PushToScreen(Bitmap bmp, int left, int top, int width, int height)
        {
            IntPtr screenDc = IntPtr.Zero;
            IntPtr memDc = IntPtr.Zero;
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;
            try
            {
                screenDc = Native.GetDC(IntPtr.Zero);
                memDc = Native.CreateCompatibleDC(screenDc);
                hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
                oldBitmap = Native.SelectObject(memDc, hBitmap);

                Native.POINT dst = new Native.POINT(left, top);
                Native.SIZE size = new Native.SIZE(width, height);
                Native.POINT src = new Native.POINT(0, 0);
                Native.BLENDFUNCTION blend = new Native.BLENDFUNCTION();
                blend.BlendOp = Native.AC_SRC_OVER;
                blend.BlendFlags = 0;
                // Whole-surface opacity. With AC_SRC_ALPHA the per-pixel alpha
                // is multiplied by this constant, so the bitmap stays opaque
                // and Opacity is applied exactly once.
                int alpha = config.Opacity;
                if (alpha < 0) alpha = 0;
                if (alpha > 255) alpha = 255;
                blend.SourceConstantAlpha = (byte)alpha;
                blend.AlphaFormat = Native.AC_SRC_ALPHA;

                Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
            }
            finally
            {
                if (memDc != IntPtr.Zero && oldBitmap != IntPtr.Zero)
                {
                    Native.SelectObject(memDc, oldBitmap);
                }
                if (hBitmap != IntPtr.Zero)
                {
                    Native.DeleteObject(hBitmap);
                }
                if (memDc != IntPtr.Zero)
                {
                    Native.DeleteDC(memDc);
                }
                if (screenDc != IntPtr.Zero)
                {
                    Native.ReleaseDC(IntPtr.Zero, screenDc);
                }
            }
        }

        private void AssertTopmost()
        {
            if (IsHandleCreated)
            {
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
        }

        private void OnTopmostTick(object sender, EventArgs e)
        {
            if (Visible)
            {
                AssertTopmost();
            }
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            try
            {
                Render();
            }
            catch (Exception)
            {
                // A transient failure during a mode switch is not fatal; the
                // next change or reload will redraw.
            }
        }

        // Re-renders, saves, and refreshes the tray icon after any setting change.
        private void ApplyChange()
        {
            Render();
            config.Save();
            BuildTrayIcon();
        }

        // --- visibility -------------------------------------------------------

        private void SetOverlayVisible(bool show)
        {
            allowVisible = show;
            if (show)
            {
                Show();
                Render();
                AssertTopmost();
            }
            else
            {
                Hide();
            }
            config.Visible = show;
            config.Save();
            UpdateToggleText();
        }

        private void ToggleVisible()
        {
            SetOverlayVisible(!allowVisible);
        }

        private void UpdateToggleText()
        {
            if (miToggle == null)
            {
                return;
            }
            miToggle.Text = allowVisible ? "Hide crosshair" : "Show crosshair";
            miToggle.ShortcutKeyDisplayString = config.HotkeyToggle.Enabled ? config.HotkeyToggle.Text : "";
        }

        // --- hotkeys ----------------------------------------------------------

        private void RegisterHotkeys()
        {
            if (!IsHandleCreated)
            {
                return;
            }
            UnregisterHotkeys();
            hotkeysRegistered = true;
            RegisterOne(HOTKEY_ID_TOGGLE, config.HotkeyToggle);
            RegisterOne(HOTKEY_ID_CYCLE_COLOR, config.HotkeyCycleColor);
            RegisterOne(HOTKEY_ID_CYCLE_STYLE, config.HotkeyCycleStyle);
        }

        private void RegisterOne(int id, HotkeySpec spec)
        {
            if (spec == null || !spec.Enabled)
            {
                return;
            }
            bool ok = Native.RegisterHotKey(Handle, id, spec.Modifiers | Native.MOD_NOREPEAT, (uint)spec.Key);
            if (!ok)
            {
                // MOD_NOREPEAT is rejected on very old systems; retry without it.
                ok = Native.RegisterHotKey(Handle, id, spec.Modifiers, (uint)spec.Key);
            }
            if (!ok)
            {
                ShowBalloon(string.Format("Hotkey {0} is in use by another program", spec.Text), ToolTipIcon.Warning);
            }
        }

        private void UnregisterHotkeys()
        {
            if (!hotkeysRegistered || !IsHandleCreated)
            {
                hotkeysRegistered = false;
                return;
            }
            Native.UnregisterHotKey(Handle, HOTKEY_ID_TOGGLE);
            Native.UnregisterHotKey(Handle, HOTKEY_ID_CYCLE_COLOR);
            Native.UnregisterHotKey(Handle, HOTKEY_ID_CYCLE_STYLE);
            hotkeysRegistered = false;
        }

        private void ShowBalloon(string text, ToolTipIcon icon)
        {
            if (trayIcon != null)
            {
                try
                {
                    trayIcon.ShowBalloonTip(4000, "Crosshair Overlay", text, icon);
                }
                catch (Exception)
                {
                    // Balloon failures are cosmetic.
                }
            }
        }

        // --- setting changes --------------------------------------------------

        private void CycleColor()
        {
            int current = -1;
            for (int i = 0; i < Config.PresetColors.Length; i++)
            {
                if (Config.PresetColors[i].ToArgb() == Color.FromArgb(255, config.Color).ToArgb())
                {
                    current = i;
                    break;
                }
            }
            int next = (current + 1) % Config.PresetColors.Length;
            config.Color = Config.PresetColors[next];
            ApplyChange();
        }

        private void CycleStyle()
        {
            int count = Enum.GetValues(typeof(CrosshairStyle)).Length;
            int next = ((int)config.Style + 1) % count;
            config.Style = (CrosshairStyle)next;
            ApplyChange();
        }

        private void ReloadConfig()
        {
            Config fresh = Config.Load(config.FilePath);
            config = fresh;
            allowVisible = config.Visible;
            RegisterHotkeys();
            if (allowVisible)
            {
                if (!Visible)
                {
                    Show();
                }
                Render();
                AssertTopmost();
            }
            else
            {
                if (Visible)
                {
                    Hide();
                }
                Render();
            }
            config.Save();
            BuildTrayIcon();
            UpdateToggleText();
        }

        // --- tray icon --------------------------------------------------------

        private void BuildTrayIcon()
        {
            if (trayIcon == null)
            {
                trayIcon = new NotifyIcon();
                trayIcon.ContextMenuStrip = menu;
                trayIcon.DoubleClick += OnTrayDoubleClick;
            }

            IntPtr newHandle;
            Icon newIcon = Renderer.CreateTrayIcon(config.Color, out newHandle);

            Icon oldIcon = trayIconObject;
            IntPtr oldHandle = trayIconHandle;

            trayIcon.Icon = newIcon;
            trayIconObject = newIcon;
            trayIconHandle = newHandle;

            string tip = "Crosshair Overlay";
            if (config.HotkeyToggle.Enabled)
            {
                tip = "Crosshair Overlay \u2014 " + config.HotkeyToggle.Text + " to toggle";
            }
            if (tip.Length > 63)
            {
                tip = tip.Substring(0, 63);
            }
            trayIcon.Text = tip;
            trayIcon.Visible = true;

            if (oldIcon != null)
            {
                oldIcon.Dispose();
            }
            if (oldHandle != IntPtr.Zero)
            {
                Native.DestroyIcon(oldHandle);
            }
        }

        private void OnTrayDoubleClick(object sender, EventArgs e)
        {
            ToggleVisible();
        }

        // --- tray menu --------------------------------------------------------

        private void BuildTrayMenu()
        {
            menu = new ContextMenuStrip();
            menu.Opening += OnMenuOpening;

            miToggle = new ToolStripMenuItem("Hide crosshair");
            miToggle.Click += OnToggleClick;
            menu.Items.Add(miToggle);
            menu.Items.Add(new ToolStripSeparator());

            // Style
            miStyle = new ToolStripMenuItem("Style");
            for (int i = 0; i < StyleNames.Length; i++)
            {
                ToolStripMenuItem item = new ToolStripMenuItem(StyleNames[i]);
                item.Tag = (CrosshairStyle)i;
                item.Click += OnStyleClick;
                miStyle.DropDownItems.Add(item);
            }
            menu.Items.Add(miStyle);

            // Color
            miColor = new ToolStripMenuItem("Color");
            for (int i = 0; i < Config.PresetNames.Length; i++)
            {
                ToolStripMenuItem item = new ToolStripMenuItem(Config.PresetNames[i]);
                item.Tag = Config.PresetColors[i];
                item.Click += OnColorClick;
                miColor.DropDownItems.Add(item);
            }
            miColor.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem customColor = new ToolStripMenuItem("Custom...");
            customColor.Click += OnCustomColorClick;
            miColor.DropDownItems.Add(customColor);
            menu.Items.Add(miColor);

            // Size
            miSize = new ToolStripMenuItem("Size");
            for (int i = 0; i < SizePresets.Length; i++)
            {
                ToolStripMenuItem item = new ToolStripMenuItem(SizeNames[i]);
                item.Tag = SizePresets[i];
                item.Click += OnSizeClick;
                miSize.DropDownItems.Add(item);
            }
            miSize.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem customSize = new ToolStripMenuItem("Custom...");
            customSize.Click += OnCustomSizeClick;
            miSize.DropDownItems.Add(customSize);
            menu.Items.Add(miSize);

            // Thickness
            miThickness = new ToolStripMenuItem("Thickness");
            for (int i = 0; i < ThicknessPresets.Length; i++)
            {
                ToolStripMenuItem item = new ToolStripMenuItem(ThicknessPresets[i].ToString(CultureInfo.InvariantCulture));
                item.Tag = ThicknessPresets[i];
                item.Click += OnThicknessClick;
                miThickness.DropDownItems.Add(item);
            }
            menu.Items.Add(miThickness);

            // Gap
            miGap = new ToolStripMenuItem("Gap");
            for (int i = 0; i < GapPresets.Length; i++)
            {
                ToolStripMenuItem item = new ToolStripMenuItem(GapPresets[i].ToString(CultureInfo.InvariantCulture));
                item.Tag = GapPresets[i];
                item.Click += OnGapClick;
                miGap.DropDownItems.Add(item);
            }
            menu.Items.Add(miGap);

            // Opacity
            miOpacity = new ToolStripMenuItem("Opacity");
            for (int i = 0; i < OpacityPercents.Length; i++)
            {
                ToolStripMenuItem item = new ToolStripMenuItem(OpacityPercents[i].ToString(CultureInfo.InvariantCulture) + "%");
                item.Tag = PercentToAlpha(OpacityPercents[i]);
                item.Click += OnOpacityClick;
                miOpacity.DropDownItems.Add(item);
            }
            menu.Items.Add(miOpacity);

            // Outline
            miOutline = new ToolStripMenuItem("Outline");
            miOutline.Click += OnOutlineClick;
            menu.Items.Add(miOutline);

            // Monitor (entries are rebuilt on every open so display changes are reflected)
            miMonitor = new ToolStripMenuItem("Monitor");
            menu.Items.Add(miMonitor);

            // Nudge
            miNudge = new ToolStripMenuItem("Nudge");
            ToolStripMenuItem nudgeUp = new ToolStripMenuItem("Up (1 px)");
            nudgeUp.Tag = new Point(0, -1);
            nudgeUp.Click += OnNudgeClick;
            ToolStripMenuItem nudgeDown = new ToolStripMenuItem("Down (1 px)");
            nudgeDown.Tag = new Point(0, 1);
            nudgeDown.Click += OnNudgeClick;
            ToolStripMenuItem nudgeLeft = new ToolStripMenuItem("Left (1 px)");
            nudgeLeft.Tag = new Point(-1, 0);
            nudgeLeft.Click += OnNudgeClick;
            ToolStripMenuItem nudgeRight = new ToolStripMenuItem("Right (1 px)");
            nudgeRight.Tag = new Point(1, 0);
            nudgeRight.Click += OnNudgeClick;
            ToolStripMenuItem nudgeReset = new ToolStripMenuItem("Reset offset");
            nudgeReset.Click += OnNudgeResetClick;
            miNudge.DropDownItems.Add(nudgeUp);
            miNudge.DropDownItems.Add(nudgeDown);
            miNudge.DropDownItems.Add(nudgeLeft);
            miNudge.DropDownItems.Add(nudgeRight);
            miNudge.DropDownItems.Add(new ToolStripSeparator());
            miNudge.DropDownItems.Add(nudgeReset);
            menu.Items.Add(miNudge);

            menu.Items.Add(new ToolStripSeparator());

            // Start with Windows
            miStartup = new ToolStripMenuItem("Start with Windows");
            miStartup.Click += OnStartupClick;
            menu.Items.Add(miStartup);

            ToolStripMenuItem openConfig = new ToolStripMenuItem("Open config file");
            openConfig.Click += OnOpenConfigClick;
            menu.Items.Add(openConfig);

            ToolStripMenuItem reload = new ToolStripMenuItem("Reload config");
            reload.Click += OnReloadClick;
            menu.Items.Add(reload);

            ToolStripMenuItem about = new ToolStripMenuItem("About");
            about.Click += OnAboutClick;
            menu.Items.Add(about);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exit = new ToolStripMenuItem("Exit");
            exit.Click += OnExitClick;
            menu.Items.Add(exit);

            UpdateToggleText();
        }

        private static int PercentToAlpha(int percent)
        {
            int a = (int)Math.Round(percent * 255.0 / 100.0);
            if (a < 0) a = 0;
            if (a > 255) a = 255;
            return a;
        }

        private void OnMenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            UpdateToggleText();

            foreach (ToolStripItem it in miStyle.DropDownItems)
            {
                ToolStripMenuItem mi = it as ToolStripMenuItem;
                if (mi != null && mi.Tag is CrosshairStyle)
                {
                    mi.Checked = (CrosshairStyle)mi.Tag == config.Style;
                }
            }

            int currentRgb = Color.FromArgb(255, config.Color).ToArgb();
            foreach (ToolStripItem it in miColor.DropDownItems)
            {
                ToolStripMenuItem mi = it as ToolStripMenuItem;
                if (mi != null && mi.Tag is Color)
                {
                    mi.Checked = ((Color)mi.Tag).ToArgb() == currentRgb;
                }
            }

            CheckIntItems(miSize, config.Size);
            CheckIntItems(miThickness, config.Thickness);
            CheckIntItems(miGap, config.Gap);
            CheckIntItems(miOpacity, config.Opacity);

            miOutline.Checked = config.Outline;

            RebuildMonitorItems();

            miStartup.Checked = IsStartupEnabled();
        }

        private static void CheckIntItems(ToolStripMenuItem parent, int current)
        {
            foreach (ToolStripItem it in parent.DropDownItems)
            {
                ToolStripMenuItem mi = it as ToolStripMenuItem;
                if (mi != null && mi.Tag is int)
                {
                    mi.Checked = (int)mi.Tag == current;
                }
            }
        }

        private void RebuildMonitorItems()
        {
            miMonitor.DropDownItems.Clear();
            Screen[] all = Screen.AllScreens;
            Screen selected = config.ResolveScreen();
            for (int i = 0; i < all.Length; i++)
            {
                Screen s = all[i];
                string label = string.Format(CultureInfo.InvariantCulture, "{0}: {1}x{2}{3}",
                    i + 1, s.Bounds.Width, s.Bounds.Height, s.Primary ? " (primary)" : "");
                ToolStripMenuItem item = new ToolStripMenuItem(label);
                item.Tag = i;
                item.Checked = s.DeviceName == selected.DeviceName;
                item.Click += OnMonitorClick;
                miMonitor.DropDownItems.Add(item);
            }
        }

        private void OnToggleClick(object sender, EventArgs e)
        {
            ToggleVisible();
        }

        private void OnStyleClick(object sender, EventArgs e)
        {
            ToolStripMenuItem mi = sender as ToolStripMenuItem;
            if (mi == null || !(mi.Tag is CrosshairStyle)) return;
            config.Style = (CrosshairStyle)mi.Tag;
            ApplyChange();
        }

        private void OnColorClick(object sender, EventArgs e)
        {
            ToolStripMenuItem mi = sender as ToolStripMenuItem;
            if (mi == null || !(mi.Tag is Color)) return;
            config.Color = (Color)mi.Tag;
            ApplyChange();
        }

        private void OnCustomColorClick(object sender, EventArgs e)
        {
            using (ColorDialog dlg = new ColorDialog())
            {
                dlg.FullOpen = true;
                dlg.AnyColor = true;
                dlg.Color = config.Color;
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    config.Color = Color.FromArgb(dlg.Color.R, dlg.Color.G, dlg.Color.B);
                    ApplyChange();
                }
            }
        }

        private void OnSizeClick(object sender, EventArgs e)
        {
            ToolStripMenuItem mi = sender as ToolStripMenuItem;
            if (mi == null || !(mi.Tag is int)) return;
            config.Size = (int)mi.Tag;
            ApplyChange();
        }

        private void OnCustomSizeClick(object sender, EventArgs e)
        {
            int value;
            if (Prompt.AskInt("Crosshair size", "Arm half-length in pixels (1-500):", 1, 500, config.Size, out value))
            {
                config.Size = value;
                ApplyChange();
            }
        }

        private void OnThicknessClick(object sender, EventArgs e)
        {
            ToolStripMenuItem mi = sender as ToolStripMenuItem;
            if (mi == null || !(mi.Tag is int)) return;
            config.Thickness = (int)mi.Tag;
            ApplyChange();
        }

        private void OnGapClick(object sender, EventArgs e)
        {
            ToolStripMenuItem mi = sender as ToolStripMenuItem;
            if (mi == null || !(mi.Tag is int)) return;
            config.Gap = (int)mi.Tag;
            ApplyChange();
        }

        private void OnOpacityClick(object sender, EventArgs e)
        {
            ToolStripMenuItem mi = sender as ToolStripMenuItem;
            if (mi == null || !(mi.Tag is int)) return;
            config.Opacity = (int)mi.Tag;
            ApplyChange();
        }

        private void OnOutlineClick(object sender, EventArgs e)
        {
            config.Outline = !config.Outline;
            ApplyChange();
        }

        private void OnMonitorClick(object sender, EventArgs e)
        {
            ToolStripMenuItem mi = sender as ToolStripMenuItem;
            if (mi == null || !(mi.Tag is int)) return;
            int idx = (int)mi.Tag;
            Screen[] all = Screen.AllScreens;
            if (idx >= 0 && idx < all.Length && all[idx].Primary)
            {
                config.Monitor = "primary";
            }
            else
            {
                config.Monitor = idx.ToString(CultureInfo.InvariantCulture);
            }
            ApplyChange();
        }

        private void OnNudgeClick(object sender, EventArgs e)
        {
            ToolStripMenuItem mi = sender as ToolStripMenuItem;
            if (mi == null || !(mi.Tag is Point)) return;
            Point d = (Point)mi.Tag;
            config.OffsetX += d.X;
            config.OffsetY += d.Y;
            ApplyChange();
        }

        private void OnNudgeResetClick(object sender, EventArgs e)
        {
            config.OffsetX = 0;
            config.OffsetY = 0;
            ApplyChange();
        }

        private void OnStartupClick(object sender, EventArgs e)
        {
            bool enabled = IsStartupEnabled();
            SetStartupEnabled(!enabled);
        }

        private void OnOpenConfigClick(object sender, EventArgs e)
        {
            string path = config.FilePath;
            if (string.IsNullOrEmpty(path))
            {
                return;
            }
            try
            {
                if (!File.Exists(path))
                {
                    config.Save();
                }
                Process.Start(path);
            }
            catch (Exception)
            {
                try
                {
                    Process.Start("notepad.exe", "\"" + path + "\"");
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Could not open the config file:\n" + path + "\n\n" + ex.Message,
                        "Crosshair Overlay", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void OnReloadClick(object sender, EventArgs e)
        {
            ReloadConfig();
        }

        private void OnAboutClick(object sender, EventArgs e)
        {
            string version = "1.0";
            try
            {
                version = Assembly.GetExecutingAssembly().GetName().Version.ToString();
            }
            catch (Exception)
            {
                // Keep the fallback string.
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Crosshair Overlay " + version);
            sb.AppendLine();
            sb.AppendLine("A static, click-through crosshair drawn at the center of the screen.");
            sb.AppendLine("It never reads or touches any other program.");
            sb.AppendLine();
            sb.AppendLine("Config file:");
            sb.AppendLine("  " + config.FilePath);
            sb.AppendLine();
            sb.AppendLine("Hotkeys:");
            sb.AppendLine("  Show / hide:   " + DescribeHotkey(config.HotkeyToggle));
            sb.AppendLine("  Cycle color:   " + DescribeHotkey(config.HotkeyCycleColor));
            sb.AppendLine("  Cycle style:   " + DescribeHotkey(config.HotkeyCycleStyle));
            sb.AppendLine();
            sb.AppendLine("Reminder: the game must run in Windowed Fullscreen (borderless).");
            sb.AppendLine("Exclusive Fullscreen bypasses the desktop and hides every overlay.");
            MessageBox.Show(sb.ToString(), "About Crosshair Overlay", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static string DescribeHotkey(HotkeySpec h)
        {
            if (h == null || !h.Enabled)
            {
                return "(disabled)";
            }
            return h.Text;
        }

        private void OnExitClick(object sender, EventArgs e)
        {
            Close();
        }

        // --- Start with Windows (HKCU Run value) ------------------------------

        private static bool IsStartupEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RUN_KEY_PATH, false))
                {
                    if (key == null)
                    {
                        return false;
                    }
                    object value = key.GetValue(RUN_VALUE_NAME);
                    return value != null;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void SetStartupEnabled(bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RUN_KEY_PATH))
                {
                    if (key == null)
                    {
                        return;
                    }
                    if (enable)
                    {
                        key.SetValue(RUN_VALUE_NAME, "\"" + Application.ExecutablePath + "\"", RegistryValueKind.String);
                    }
                    else
                    {
                        key.DeleteValue(RUN_VALUE_NAME, false);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not update the Start with Windows setting:\n" + ex.Message,
                    "Crosshair Overlay", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // --- cleanup ----------------------------------------------------------

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                UnregisterHotkeys();

                if (topmostTimer != null)
                {
                    topmostTimer.Stop();
                    topmostTimer.Dispose();
                    topmostTimer = null;
                }
                if (trayIcon != null)
                {
                    trayIcon.Visible = false;
                    trayIcon.Dispose();
                    trayIcon = null;
                }
                if (trayIconObject != null)
                {
                    trayIconObject.Dispose();
                    trayIconObject = null;
                }
                if (trayIconHandle != IntPtr.Zero)
                {
                    Native.DestroyIcon(trayIconHandle);
                    trayIconHandle = IntPtr.Zero;
                }
                if (menu != null)
                {
                    menu.Dispose();
                    menu = null;
                }
            }
            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------------
    // Entry point
    // ------------------------------------------------------------------

    internal static class Program
    {
        private const string MutexName = @"Local\CrosshairOverlay";

        [STAThread]
        private static void Main()
        {
            Mutex mutex = null;
            bool createdNew = true;
            try
            {
                mutex = new Mutex(true, MutexName, out createdNew);
            }
            catch (Exception)
            {
                // If the mutex cannot be created for any reason, run anyway.
                mutex = null;
                createdNew = true;
            }

            if (!createdNew)
            {
                // Another instance is already running. Exit silently.
                if (mutex != null)
                {
                    mutex.Dispose();
                }
                return;
            }

            try
            {
                EnableDpiAwareness();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                string path = Config.ResolvePath();
                Config config = Config.Load(path);
                config.Save();

                Application.Run(new OverlayForm(config));
            }
            catch (Exception ex)
            {
                try
                {
                    MessageBox.Show("Crosshair Overlay failed to start:\n\n" + ex.ToString(),
                        "Crosshair Overlay", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch (Exception)
                {
                    // Nothing more can be done.
                }
            }
            finally
            {
                if (mutex != null)
                {
                    try
                    {
                        mutex.ReleaseMutex();
                    }
                    catch (Exception)
                    {
                        // Already released or not owned; ignore.
                    }
                    mutex.Dispose();
                }
            }
        }

        // The manifest already declares PerMonitorV2, so this call normally
        // reports "already set". It exists so the process is still DPI aware
        // when the exe is run without its embedded manifest (for example after
        // being rebuilt by hand).
        private static void EnableDpiAwareness()
        {
            try
            {
                Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            }
            catch (EntryPointNotFoundException)
            {
                try
                {
                    Native.SetProcessDPIAware();
                }
                catch (Exception)
                {
                    // Not available; continue without DPI awareness.
                }
            }
            catch (Exception)
            {
                // Any other failure is non-fatal.
            }
        }
    }
}
