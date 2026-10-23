using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace Scriblucent.Packaging
{
    // Build-time palette conversion of the high-DPI styles supplied by Inno Setup 6.7.3.
    // The original author, version and website metadata are retained. No runtime hooks are used.
    public static class ControlStylePalette
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindResource(IntPtr module, string name, string type);
        [DllImport("kernel32.dll")] static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
        [DllImport("kernel32.dll")] static extern IntPtr LockResource(IntPtr resource);
        [DllImport("kernel32.dll")] static extern uint SizeofResource(IntPtr module, IntPtr resource);
        [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);

        sealed class Texture
        {
            public string Name;
            public int Width, Height, Offset;
        }

        static string ReadString(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > 1024) throw new InvalidDataException("Invalid style string.");
            return Encoding.Unicode.GetString(reader.ReadBytes(checked(length * 2)));
        }

        static void WriteString(BinaryWriter writer, string value)
        {
            writer.Write(value.Length);
            writer.Write(Encoding.Unicode.GetBytes(value));
        }

        static byte[] ReadResource(IntPtr module, string name)
        {
            IntPtr entry = FindResource(module, name, "VCLSTYLE");
            if (entry == IntPtr.Zero) throw new InvalidDataException("Missing compiler style: " + name);
            byte[] bytes = new byte[SizeofResource(module, entry)];
            Marshal.Copy(LockResource(LoadResource(module, entry)), bytes, 0, bytes.Length);
            return bytes;
        }

        // Neutral surfaces stay neutral. Blue/cyan controls and progress fills adopt brand blue.
        static int Accent(int rgb)
        {
            int r = (rgb >> 16) & 255, g = (rgb >> 8) & 255, b = rgb & 255;
            bool blue = b - r >= 28 && b - g >= -12 && g >= r;
            bool progress = g - r >= 28 && g - b >= 15;
            if (!blue && !progress) return rgb;
            double light = (Math.Max(r, Math.Max(g, b)) + Math.Min(r, Math.Min(g, b))) / 510.0;
            // Preserve antialiasing and light/dark contrast along the brand's tonal scale.
            double baseLight = (67 + 236) / 510.0;
            int[] brand = { 67, 92, 236 };
            int result = 0;
            foreach (int channel in brand)
            {
                double value = light <= baseLight ? channel * light / baseLight :
                    channel + (255 - channel) * (light - baseLight) / (1 - baseLight);
                result = (result << 8) | Math.Max(0, Math.Min(255, (int)Math.Round(value)));
            }
            return result;
        }

        static int Blend(int rgb, double strength)
        {
            int result = 0;
            int[] brand = { 67, 92, 236 };
            for (int i = 0; i < 3; i++)
            {
                int channel = (rgb >> (16 - 8 * i)) & 255;
                result = (result << 8) | (int)Math.Round(channel * (1 - strength) + brand[i] * strength);
            }
            return result;
        }

        static int Pixel(byte[] bytes, int offset)
        {
            return (bytes[offset + 2] << 16) | (bytes[offset + 1] << 8) | bytes[offset];
        }

        static void Pixel(byte[] bytes, int offset, int rgb)
        {
            bytes[offset] = (byte)rgb;
            bytes[offset + 1] = (byte)(rgb >> 8);
            bytes[offset + 2] = (byte)(rgb >> 16);
        }

        static int AlphaScale(int rgb, int numerator, int denominator)
        {
            int result = 0;
            for (int shift = 16; shift >= 0; shift -= 8)
                result = (result << 8) | Math.Min(255, ((((rgb >> shift) & 255) * numerator) + denominator / 2) / denominator);
            return result;
        }

        static int Property(byte[] data, int start, int end, string name)
        {
            byte[] key = Encoding.ASCII.GetBytes(name);
            for (int i = start; i + key.Length + 5 < end; i++)
            {
                if (data[i] != key.Length) continue;
                int n = 0;
                while (n < key.Length && data[i + 1 + n] == key[n]) n++;
                if (n == key.Length) return i + 1 + key.Length;
            }
            throw new InvalidDataException("Missing style property: " + name);
        }

        static int IntegerProperty(byte[] data, int start, int end, string name)
        {
            int offset = Property(data, start, end, name);
            switch (data[offset])
            {
                case 2: return (sbyte)data[offset + 1];
                case 3: return BitConverter.ToInt16(data, offset + 1);
                case 4: return BitConverter.ToInt32(data, offset + 1);
                default: throw new InvalidDataException("Unexpected style integer type.");
            }
        }

        static void TintButton(byte[] data, int start, int end, List<Texture> textures, bool dark)
        {
            // The first bitmap in the Button definition is its face, followed by command-link glyphs.
            Texture texture = textures[0];
            foreach (string state in new[] { "Bitmap", "BitmapHot", "BitmapFocused", "BitmapPressed" })
            {
                int left = IntegerProperty(data, start, end, state + ".Left");
                int top = IntegerProperty(data, start, end, state + ".Top");
                int right = IntegerProperty(data, start, end, state + ".Right");
                int bottom = IntegerProperty(data, start, end, state + ".Bottom");
                if (left < 0 || top < 0 || right > texture.Width || bottom > texture.Height)
                    throw new InvalidDataException("Invalid button texture bounds.");
                double strength = state == "Bitmap" ? (dark ? .18 : .08) : (dark ? .38 : .20);
                for (int y = top; y < bottom; y++)
                    for (int x = left; x < right; x++)
                    {
                        // VCL bitmap scan lines are stored bottom-up.
                        int offset = texture.Offset + 4 * ((texture.Height - 1 - y) * texture.Width + x);
                        int alpha = data[offset + 3];
                        if (alpha == 0) continue;
                        int straight = AlphaScale(Pixel(data, offset), 255, alpha);
                        Pixel(data, offset, AlphaScale(Blend(straight, strength), alpha, 255));
                    }
            }
        }

        static void RecolorProperties(byte[] data, int start, int end)
        {
            // Delphi binary component streams encode numeric Color/Font.Color with vaInt32.
            foreach (string name in new[] { "Color", "Font.Color" })
            {
                byte[] key = Encoding.ASCII.GetBytes(name);
                for (int i = start; i + key.Length + 6 < end; i++)
                {
                    if (data[i] != key.Length) continue;
                    int n = 0;
                    while (n < key.Length && data[i + 1 + n] == key[n]) n++;
                    int value = i + 1 + key.Length;
                    if (n != key.Length || data[value] != 4 || data[value + 4] != 0) continue;
                    int bgr = BitConverter.ToInt32(data, value + 1);
                    int rgb = ((bgr & 255) << 16) | (bgr & 0xFF00) | ((bgr >> 16) & 255);
                    int mapped = Accent(rgb);
                    int changed = ((mapped & 255) << 16) | (mapped & 0xFF00) | ((mapped >> 16) & 255);
                    Buffer.BlockCopy(BitConverter.GetBytes(changed), 0, data, value + 1, 4);
                }
            }
        }

        static string MapColor(string value, string name, bool dark)
        {
            if (value.Length != 9 || value[0] != '$') return value;
            int bgr = int.Parse(value.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int rgb = ((bgr & 255) << 16) | (bgr & 0xFF00) | ((bgr >> 16) & 255);
            int mapped = Accent(rgb);
            if (name == "FocusEffect" || name == "clHighlight") mapped = 0x435CEC;
            if (name.StartsWith("Button", StringComparison.Ordinal) && name != "ButtonDisabled")
                mapped = Blend(mapped, name == "ButtonNormal" ? (dark ? .18 : .08) : (dark ? .38 : .20));
            int output = ((mapped & 255) << 16) | (mapped & 0xFF00) | ((mapped >> 16) & 255);
            return "$" + output.ToString("X8", CultureInfo.InvariantCulture);
        }

        static void Dictionary(BinaryReader reader, BinaryWriter writer, int count, bool colors, bool dark)
        {
            for (int i = 0; i < count; i++)
            {
                string key = ReadString(reader), separator = ReadString(reader), value = ReadString(reader);
                if (separator != ":") throw new InvalidDataException("Invalid style dictionary.");
                WriteString(writer, key); WriteString(writer, separator);
                WriteString(writer, colors ? MapColor(value, key, dark) : value);
            }
        }

        static byte[] Palette(byte[] packed, bool dark)
        {
            const string signature = "VCL_STYLE 2.0";
            if (Encoding.ASCII.GetString(packed, 0, 13) != signature)
                throw new InvalidDataException("Unsupported style format.");
            byte[] data;
            using (var input = new MemoryStream(packed, 15, packed.Length - 19))
            using (var inflate = new DeflateStream(input, CompressionMode.Decompress))
            using (var raw = new MemoryStream()) { inflate.CopyTo(raw); data = raw.ToArray(); }
            using (var reader = new BinaryReader(new MemoryStream(data)))
            using (var output = new MemoryStream())
            using (var writer = new BinaryWriter(output))
            {
                ReadString(reader);
                WriteString(writer, "Scriblucent " + (dark ? "Dark" : "Light"));
                for (int i = 0; i < 5; i++) WriteString(writer, ReadString(reader));
                // Style engine tag, followed by its three high-DPI bitmap atlases.
                byte[] engine = reader.ReadBytes(23);
                if (Encoding.ASCII.GetString(engine, 10, 13) != "MasterStyle\r\n")
                    throw new InvalidDataException("Unsupported style engine.");
                writer.Write(engine);
                int textureCount = reader.ReadInt32();
                if (textureCount != 3) throw new InvalidDataException("Missing high-DPI style atlases.");
                writer.Write(textureCount);
                int texturesStart = (int)reader.BaseStream.Position;
                var textures = new List<Texture>();
                for (int i = 0; i < textureCount; i++)
                {
                    var t = new Texture { Name = ReadString(reader), Width = reader.ReadInt32(), Height = reader.ReadInt32() };
                    if (t.Width < 1 || t.Height < 1 || t.Width > 3000 || t.Height > 3000)
                        throw new InvalidDataException("Invalid style atlas dimensions.");
                    t.Offset = (int)reader.BaseStream.Position;
                    textures.Add(t);
                    reader.BaseStream.Position += checked(t.Width * t.Height * 4 + 2);
                }
                int objects = reader.ReadInt32();
                if (objects != 33) throw new InvalidDataException("Unexpected control definitions.");
                foreach (Texture t in textures)
                    for (int offset = t.Offset; offset < t.Offset + t.Width * t.Height * 4; offset += 4)
                    {
                        int alpha = data[offset + 3];
                        if (alpha == 0) continue;
                        int straight = AlphaScale(Pixel(data, offset), 255, alpha);
                        int mapped = Accent(straight);
                        if (mapped != straight) Pixel(data, offset, AlphaScale(mapped, alpha, 255));
                    }
                for (int i = 0; i < objects; i++)
                {
                    ReadString(reader);
                    int length = reader.ReadInt32(), start = (int)reader.BaseStream.Position;
                    int classLength = data[start + 4], nameLength = data[start + 5 + classLength];
                    string name = Encoding.ASCII.GetString(data, start + 6 + classLength, nameLength);
                    if (name == "Button") TintButton(data, start, start + length, textures, dark);
                    RecolorProperties(data, start, start + length);
                    reader.BaseStream.Position += length;
                }
                // Copy the atlases and control trees with their geometry and alpha coverage intact.
                int tablesStart = (int)reader.BaseStream.Position;
                writer.Write(data, texturesStart, tablesStart - texturesStart);
                byte colorMax = reader.ReadByte(); writer.Write(colorMax);
                Dictionary(reader, writer, colorMax + 1, true, dark);
                int systemColors = reader.ReadInt32(); writer.Write(systemColors);
                Dictionary(reader, writer, systemColors, true, dark);
                byte fontMax = reader.ReadByte(); writer.Write(fontMax);
                Dictionary(reader, writer, fontMax + 1, false, dark);
                if (reader.BaseStream.Position != data.Length) throw new InvalidDataException("Unexpected style data.");
                writer.Flush();
                return Pack(output.ToArray());
            }
        }

        static byte[] Pack(byte[] raw)
        {
            using (var output = new MemoryStream())
            {
                byte[] header = Encoding.ASCII.GetBytes("VCL_STYLE 2.0");
                output.Write(header, 0, header.Length);
                output.WriteByte(0x78); output.WriteByte(0x9C);
                using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, true))
                    deflate.Write(raw, 0, raw.Length);
                uint a = 1, b = 0;
                foreach (byte value in raw) { a = (a + value) % 65521; b = (b + a) % 65521; }
                uint checksum = (b << 16) | a;
                for (int shift = 24; shift >= 0; shift -= 8) output.WriteByte((byte)(checksum >> shift));
                return output.ToArray();
            }
        }

        public static void Build(string resourceImage, string outputDirectory)
        {
            // LOAD_LIBRARY_AS_DATAFILE never executes compiler code.
            IntPtr module = LoadLibraryEx(resourceImage, IntPtr.Zero, 2);
            if (module == IntPtr.Zero) throw new InvalidDataException("Cannot read compiler styles.");
            try
            {
                File.WriteAllBytes(Path.Combine(outputDirectory, "scriblucent-light.vsf"), Palette(ReadResource(module, "WINDOWSMODERNLIGHT"), false));
                File.WriteAllBytes(Path.Combine(outputDirectory, "scriblucent-dark.vsf"), Palette(ReadResource(module, "WINDOWSMODERNDARK"), true));
            }
            finally { FreeLibrary(module); }
        }
    }
}
