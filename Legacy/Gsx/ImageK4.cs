//! \file       ImageK2.cs
//! \date       2018 Feb 09
//! \brief      Toyo GSX image format.
//
// Copyright (C) 2018 by morkt
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to
// deal in the Software without restriction, including without limitation the
// rights to use, copy, modify, merge, publish, distribute, sublicense, and/or
// sell copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
// IN THE SOFTWARE.
//

using System;
using System.ComponentModel.Composition;
using System.IO;
using GameRes.Utility;

namespace GameRes.Formats.Gsx
{
    [Export(typeof(ImageFormat))]
    public class K4Format : ImageFormat
    {
        public override string         Tag { get { return "K4"; } }
        public override string Description { get { return "Toyo GSX image format"; } }
        public override uint     Signature { get { return 0x0201344B; } } // 'K4'

        public K4Format()
        {
           //Signatures = new uint[] { 0x18324B, 0x20324B, 0x10324B, 0x0F324B, 0x08324B, 0x04324B, 0x01324B };
        }

        public override ImageMetaData ReadMetaData (IBinaryStream file)
        {
            var tga_header = new byte[0x12];
            tga_header[2] = 2; // raw image type
            file.Position = 0x30;
            for (int i = 0xC; i < 0xC + 4; ++i)
            {
                tga_header[i] = file.ReadUInt8();
            }
            file.Position = 0x3C;
            byte bpp = file.ReadUInt8();
            tga_header[0x10] = bpp;
            using (BinMemoryStream tga = new BinMemoryStream(tga_header))
            {
                return Tga.ReadMetaData(tga);
            }
        }

        public override ImageData Read (IBinaryStream file, ImageMetaData info)
        {
            byte[] tga_data = Decompress(file);
            using (BinMemoryStream tga = new BinMemoryStream(tga_data))
            {
                return Tga.Read(tga, info);
            }
        }

        public override void Write (Stream file, ImageData image)
        {
            throw new System.NotImplementedException ("K4Format.Write not implemented");
        }

        internal byte[] Decompress (IBinaryStream input)
        {
            input.Position = 0x40;
            int unpacked_size = input.ReadInt32();
            return Decompress (input, unpacked_size);
        }

        internal bool ADC(bool cf, ref byte input) {
            bool is_overflow = (input & 0x80) != 0;
            input = (byte) ((input << 1) & 0xFF);
            if (cf) input++;
            return is_overflow;
        }
        internal bool ADC32(bool cf, ref uint input)
        {
            bool is_overflow = (input & 0x80000000) != 0;
            input <<= 1;
            if (cf) input++;
            return is_overflow;
        }
        internal byte[] Decompress(IBinaryStream input, int unpacked_size)
        {
            input.Position = 0x18;
            uint file_size = input.ReadUInt32();
            input.Position = 0x32;
            uint height = input.ReadUInt16();
            input.Position = 0x3C;
            byte bpp = input.ReadUInt8();
            input.Position = 0x3E;
            uint byte_3e = input.ReadUInt8();
            if (byte_3e != 0) byte_3e = (uint)-(bpp >> 3);
            input.Position = 0x50;
            int bits = input.ReadInt32();
            input.Position = 0x54;
            int data_pos = input.ReadInt32();  // const
            input.Position = 0x58;
            int ctl_bits_length = Math.Min(data_pos - 0x10, (bits + 7) / 8); // const

            // All the points needed
            var ctl_stream = new BinaryStream(new MemoryStream(
                input.ReadBytes(ctl_bits_length)), "");
            input.Position = 0x4c;

            // exit criterial.
            // bits < 0 or remaining_data_size < 0 or max_bit_count < 0.
            int remaining_data_size = input.ReadInt32() - data_pos; // const
            uint left = (uint)((file_size - 0x28) * (bpp >> 3) + 3) & 0xFFFFFFFC;
            uint max_bit_count = left * (uint)height;

            // Result.
            byte[] result_output = new byte[unpacked_size + 0x12c];
            // Build TGA header.
            result_output[2] = 2; // raw image type
            input.Position = 0x30;
            for (int i = 0xC; i < 0xC + 4; ++i)
            {
                result_output[i] = input.ReadUInt8();
            }
            result_output[0x10] = bpp;
            // Offset relative to 0x12 index of result_output.
            uint output_offset = 0;

            // Stores current byte from data section.
            byte data_byte = 0;
            // Stores current byte from control section.
            byte ctl_byte = 0;

            // Set input pointer to the start of the data
            input.Position = data_pos + 0x48;
            // Common Functions
            bool func_3()
            {
                bits--;
                bool cf = false;
                if (bits < 0) return cf;
                cf = (ctl_byte & 0x80) != 0;
                ctl_byte <<= 1;
                if (ctl_byte == 0)
                {
                    ctl_byte = ctl_stream.ReadUInt8();
                    cf = ADC(true, ref ctl_byte);
                }
                return cf;
            }
            // Returns whether we should exit;
            uint func_5(uint iterations, ref bool cf)
            {
                uint result = 0;
                // @@5a
                while (iterations > 0)
                {
                    cf = (data_byte & 0x80) != 0;
                    data_byte <<= 1;
                    if (data_byte == 0)
                    {
                        remaining_data_size--;
                        if (remaining_data_size < 0) return result;
                        data_byte = input.ReadUInt8();
                        cf = ADC(true, ref data_byte);
                    }
                    // END @@5a
                    // @@5b
                    cf = ADC32(cf, ref result);
                    iterations--;
                }
                return result;
            }

            bool func_2c(uint iterations, uint edx)
            {
                while (iterations > 0)
                {
                    bool is_overflow = (0xFFFFFFFF - output_offset) < byte_3e;
                    uint temp_output_index = output_offset + edx;
                    if (is_overflow)
                    {
                        // @@2e
                        bool is_edx_overflow = (0xFFFFFFFF - output_offset) < edx;
                        if (!is_edx_overflow) return true;
                        uint tmp_offset = edx + byte_3e;
                        // END @@2e
                        // @@2f
                        while (iterations > 0)
                        {
                            byte tmp_output = result_output[0x12 + output_offset + edx];
                            tmp_output -= result_output[0x12 + output_offset + tmp_offset];
                            tmp_output += result_output[0x12 + output_offset + byte_3e];
                            result_output[0x12 + output_offset] = tmp_output;
                            output_offset++;
                            iterations--;
                        }
                        // END @@2f
                        return false;
                    }
                    else
                    {
                        result_output[0x12 + output_offset] = result_output[0x12 + temp_output_index];
                        output_offset++;
                        iterations--;
                    }
                }
                return false;
            }

            // This is where the unpack starts
            // @@1
            while (max_bit_count > 0)
            {
                bool cf = func_3();
                if (bits < 0) break;
                uint ecx;
                if (cf)
                {
                    --max_bit_count;
                    if (max_bit_count <= 0) break;
                    ecx = 8;
                    if (byte_3e != 0)
                    {
                        // @@2a
                        ecx++;
                        cf = (0xFFFFFFFF - output_offset) < byte_3e;
                        uint temp_output_index = output_offset + byte_3e;

                        if (cf)
                        {
                            uint tmp_result = func_5(ecx, ref cf) + 1;
                            if (remaining_data_size < 0) break;
                            result_output[0x12 + output_offset] = (byte)(tmp_result + result_output[0x12 + temp_output_index]);
                            output_offset++;
                            continue;
                        }
                    }
                    cf = false;
                    // @@2b
                    uint tmp = func_5(ecx, ref cf);
                    if (remaining_data_size < 0) break;
                    result_output[0x12 + output_offset] = (byte)tmp;
                    output_offset++;
                    // END @@2b
                }
                else
                {
                    // @@1a
                    cf = func_3();
                    if (bits < 0) break;
                    uint edx;
                    if (cf)
                    {
                        // @@1b
                        edx = func_5(0xE, ref cf);
                        if (remaining_data_size < 0) break;
                        uint tmp = func_5(4, ref cf);
                        if (remaining_data_size < 0) break;
                        ecx = tmp + 3;
                        // END @@1b
                    }
                    else
                    {
                        edx = func_5(9, ref cf);
                        if (remaining_data_size < 0) break;
                        // xchg edx, eax
                        uint tmp = func_5(3, ref cf);
                        if (remaining_data_size < 0) break;
                        ecx = tmp + 2;
                    }
                    // END @@1a
                    // @@1c
                    edx = ~edx;
                    if (max_bit_count <= 0) break;
                    cf = ecx <= max_bit_count;
                    if (ecx > max_bit_count)
                    {
                        ecx = max_bit_count;
                    }
                    //@@1c_sub
                    max_bit_count -= ecx;
                    cf = (0xFFFFFFFF - output_offset) < edx;
                    if (!cf) break;
                    cf = byte_3e != 0;
                    if (byte_3e != 0)
                    {
                        if (func_2c(ecx, edx)) break;
                        continue;
                    }
                    uint temp_output_index = output_offset + edx;
                    for (int i = 0; i < ecx; ++i)
                    {
                        result_output[0x12 + output_offset] = result_output[0x12 + temp_output_index];
                        output_offset++;
                        temp_output_index++;
                    }
                }
            }
            return result_output;
        }
    }
}
