/*
 * Original code by Kevin Weiner, FM Software.
 *                  Thomas Hourdel.
 * Adapted by Helge Backhaus
 */

using System;
using System.IO;
using UnityEngine;

namespace SnapXR.Encoder
{
    public class GifEncoder
    {
        protected int m_Width;
        protected int m_Height;
        protected int m_Repeat = -1;                  // -1: no repeat, 0: infinite, >0: repeat count
        protected int m_FrameDelay = 0;               // Frame delay (milliseconds)
        protected bool m_HasStarted = false;          // Ready to output frames
        protected FileStream m_FileStream;

        protected byte[] m_Pixels;                    // BGR byte array from frame
        protected byte[] m_IndexedPixels;             // Converted frame indexed to palette
        protected int m_ColorDepth;                   // Number of bit planes
        protected byte[] m_ColorTab;                  // RGB palette
        protected bool[] m_UsedEntry = new bool[256]; // Active palette entries
        protected int m_PaletteSize = 7;              // Color table size (bits-1)
        protected int m_DisposalCode = -1;            // Disposal code (-1 = use default)
        protected bool m_IsFirstFrame = true;
        protected int m_SampleInterval = 10;          // Default sample interval for quantizer

        /// <summary>
        /// Constructor with the number of times the set of GIF frames should be played.
        /// </summary>
        /// <param name="width">Gif width</param>
        /// <param name="height">Gif height</param>
        /// <param name="repeat">Default is -1 (no repeat); 0 means play indefinitely</param>
        /// <param name="quality">Sets quality of color quantization (conversion of images to
        /// the maximum 256 colors allowed by the GIF specification). Lower values (minimum = 1)
        /// produce better colors, but slow processing significantly. Higher values will speed
        /// up the quantization pass at the cost of lower image quality (maximum = 100).</param>
        /// <param name="ms">Delay time in milliseconds</param>
        public GifEncoder(int width, int height, int repeat, int quality, int ms)
        {
            if (repeat >= 0)
                m_Repeat = repeat;

            m_SampleInterval = (int)Mathf.Clamp(quality, 1, 100);
            m_Width = width;
            m_Height = height;
            m_FrameDelay = Mathf.RoundToInt(ms / 10f);
        }

        /// <summary>
        /// Adds next GIF frame. The frame is not written immediately, but is actually deferred
        /// until the next frame is received so that timing data can be inserted. Invoking
        /// <code>Finish()</code> flushes all frames.
        /// </summary>
        /// <param name="pixels">GifFrame color data to write.</param>
        public void AddFrame(byte[] pixels)
        {
            if ((pixels == null))
                throw new ArgumentNullException("Can't add a null frame to the gif.");

            if (!m_HasStarted)
                throw new InvalidOperationException("Call Start() before adding frames to the gif.");

            m_Pixels = pixels;
            AnalyzePixels();

            if (m_IsFirstFrame)
            {
                WriteLSD();
                WritePalette();

                if (m_Repeat >= 0)
                    WriteNetscapeExt();
            }

            WriteGraphicCtrlExt();
            WriteImageDesc();

            if (!m_IsFirstFrame)
                WritePalette();

            WritePixels();
            m_IsFirstFrame = false;
        }

        /// <summary>
        /// Initiates writing of a GIF file with the specified name. The stream will be handled for you.
        /// </summary>
        /// <param name="file">String containing output file name</param>
        public void Start(String file)
        {
            try
            {
                m_FileStream = new FileStream(file, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);

                if (m_FileStream == null)
                    throw new ArgumentNullException("Stream is null.");

                WriteString("GIF89a"); // header

                m_HasStarted = true;
            }
            catch (IOException e)
            {
                throw e;
            }
        }

        /// <summary>
        /// Flushes any pending data and closes output file.
        /// If writing to an OutputStream, the stream is not closed.
        /// </summary>
        public void Finish()
        {
            if (!m_HasStarted)
                throw new InvalidOperationException("Can't finish a non-started gif.");

            m_HasStarted = false;

            try
            {
                m_FileStream.WriteByte(0x3b); // Gif trailer
                m_FileStream.Flush();

                m_FileStream.Close();
            }
            catch (IOException e)
            {
                throw e;
            }

            // Reset for subsequent use
            m_FileStream = null;
            m_Pixels = null;
            m_IndexedPixels = null;
            m_ColorTab = null;
            m_IsFirstFrame = true;
        }

        // Analyzes image colors and creates color map.
        protected void AnalyzePixels()
        {
            int len = m_Pixels.Length;
            int nPix = len / 3;
            m_IndexedPixels = new byte[nPix];
            NeuQuant nq = new NeuQuant(m_Pixels, len, (int)m_SampleInterval);
            m_ColorTab = nq.Process(); // Create reduced palette

            // Map image pixels to new palette
            int k = 0;
            for (int i = 0; i < nPix; i++)
            {
                int index = nq.Map(m_Pixels[k++] & 0xff, m_Pixels[k++] & 0xff, m_Pixels[k++] & 0xff);
                m_UsedEntry[index] = true;
                m_IndexedPixels[i] = (byte)index;
            }

            m_Pixels = null;
            m_ColorDepth = 8;
            m_PaletteSize = 7;
        }

        // Writes Graphic Control Extension.
        protected void WriteGraphicCtrlExt()
        {
            m_FileStream.WriteByte(0x21); // Extension introducer
            m_FileStream.WriteByte(0xf9); // GCE label
            m_FileStream.WriteByte(4);    // Data block size

            // Packed fields
            m_FileStream.WriteByte(Convert.ToByte(0 |     // 1:3 reserved
                                                  0 |     // 4:6 disposal
                                                  0 |     // 7   user input - 0 = none
                                                  0));    // 8   transparency flag

            WriteShort(m_FrameDelay); // Delay x 1/100 sec
            m_FileStream.WriteByte(Convert.ToByte(0)); // Transparent color index
            m_FileStream.WriteByte(0); // Block terminator
        }

        // Writes Image Descriptor.
        protected void WriteImageDesc()
        {
            m_FileStream.WriteByte(0x2c); // Image separator
            WriteShort(0);                // Image position x,y = 0,0
            WriteShort(0);
            WriteShort(m_Width);          // image size
            WriteShort(m_Height);

            // Packed fields
            if (m_IsFirstFrame)
            {
                m_FileStream.WriteByte(0); // No LCT  - GCT is used for first (or only) frame
            }
            else
            {
                // Specify normal LCT
                m_FileStream.WriteByte(Convert.ToByte(0x80 |           // 1 local color table  1=yes
                                                      0 |              // 2 interlace - 0=no
                                                      0 |              // 3 sorted - 0=no
                                                      0 |              // 4-5 reserved
                                                      m_PaletteSize)); // 6-8 size of color table
            }
        }

        // Writes Logical Screen Descriptor.
        protected void WriteLSD()
        {
            // Logical screen size
            WriteShort(m_Width);
            WriteShort(m_Height);

            // Packed fields
            m_FileStream.WriteByte(Convert.ToByte(0x80 |           // 1   : global color table flag = 1 (gct used)
                                                  0x70 |           // 2-4 : color resolution = 7
                                                  0x00 |           // 5   : gct sort flag = 0
                                                  m_PaletteSize)); // 6-8 : gct size

            m_FileStream.WriteByte(0); // Background color index
            m_FileStream.WriteByte(0); // Pixel aspect ratio - assume 1:1
        }

        // Writes Netscape application extension to define repeat count.
        protected void WriteNetscapeExt()
        {
            m_FileStream.WriteByte(0x21);    // Extension introducer
            m_FileStream.WriteByte(0xff);    // App extension label
            m_FileStream.WriteByte(11);      // Block size
            WriteString("NETSCAPE" + "2.0"); // App id + auth code
            m_FileStream.WriteByte(3);       // Sub-block size
            m_FileStream.WriteByte(1);       // Loop sub-block id
            WriteShort(m_Repeat);            // Loop count (extra iterations, 0=repeat forever)
            m_FileStream.WriteByte(0);       // Block terminator
        }

        // Write color table.
        protected void WritePalette()
        {
            m_FileStream.Write(m_ColorTab, 0, m_ColorTab.Length);
            int n = (3 * 256) - m_ColorTab.Length;

            for (int i = 0; i < n; i++)
                m_FileStream.WriteByte(0);
        }

        // Encodes and writes pixel data.
        protected void WritePixels()
        {
            LzwEncoder encoder = new LzwEncoder(m_Width, m_Height, m_IndexedPixels, m_ColorDepth);
            encoder.Encode(m_FileStream);
        }

        // Write 16-bit value to output stream, LSB first.
        protected void WriteShort(int value)
        {
            m_FileStream.WriteByte(Convert.ToByte(value & 0xff));
            m_FileStream.WriteByte(Convert.ToByte((value >> 8) & 0xff));
        }

        // Writes string to output stream.
        protected void WriteString(String s)
        {
            char[] chars = s.ToCharArray();

            for (int i = 0; i < chars.Length; i++)
                m_FileStream.WriteByte((byte)chars[i]);
        }
    }

    public class GifFrame
    {
        public int Width;
        public int Height;
        public Color32[] Data;
        public uint ID;

        // Extracts image pixels into byte array "pixels".
        public byte[] ExtractImagePixels()
        {
            byte[] pixels = new Byte[3 * Width * Height];
            int count = 0;
            // Texture data is layered down-top, so flip it
            for (int th = Height - 1; th >= 0; th--)
            {
                for (int tw = 0; tw < Width; tw++)
                {
                    Color32 color = Data[th * Width + tw];
                    pixels[count] = color.r; count++;
                    pixels[count] = color.g; count++;
                    pixels[count] = color.b; count++;
                }
            }
            return pixels;
        }
    }
}
