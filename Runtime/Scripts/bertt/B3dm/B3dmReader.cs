using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
namespace Netherlands3D.Tiles3D
{
    public static class B3dmReader
    {
        public static B3dm ReadB3dm(BinaryReader reader)
        {
            var b3dmHeader = new B3dmHeader(reader);
            var featureTableJson = Encoding.UTF8.GetString(reader.ReadBytes(b3dmHeader.FeatureTableJsonByteLength));
            var featureTableBytes = reader.ReadBytes(b3dmHeader.FeatureTableBinaryByteLength);

            string batchTableJson = null;
            if (b3dmHeader.BatchTableJsonByteLength != 0)
            {
                batchTableJson = Encoding.UTF8.GetString(reader.ReadBytes(b3dmHeader.BatchTableJsonByteLength));
            }
            byte[] batchTableBytes = null;
            if (b3dmHeader.BatchTableBinaryByteLength != 0)
            {
                batchTableBytes = reader.ReadBytes(b3dmHeader.BatchTableBinaryByteLength);
            }


            // Read GLB efficiently: first read 12-byte header to get total GLB length,
            // then read exactly that many bytes, avoiding an extra large buffer + trim copy.
            var remaining = b3dmHeader.fileLength - b3dmHeader.Length;
            if (remaining < 12)
            {
                throw new EndOfStreamException("B3DM GLB segment too small to contain header");
            }

            // Read GLB header (12 bytes)
            byte[] glbHeader = reader.ReadBytes(12);
            if (glbHeader.Length != 12)
            {
                throw new EndOfStreamException("Failed to read GLB header from B3DM");
            }

            // Total GLB length stored at bytes 8..11 (little endian)
            int totalGlbLength = glbHeader[11] * 256;
            totalGlbLength = (totalGlbLength + glbHeader[10]) * 256;
            totalGlbLength = (totalGlbLength + glbHeader[9]) * 256;
            totalGlbLength = totalGlbLength + glbHeader[8];

            if (totalGlbLength < 12)
            {
                throw new InvalidDataException($"Invalid GLB length {totalGlbLength}");
            }

            byte[] glbBuffer = new byte[totalGlbLength];
            // Copy header
            Buffer.BlockCopy(glbHeader, 0, glbBuffer, 0, 12);
            int bytesToRead = totalGlbLength - 12;
            int read = reader.Read(glbBuffer, 12, bytesToRead);
            if (read != bytesToRead)
            {
                throw new EndOfStreamException($"Expected {bytesToRead} GLB bytes, got {read}");
            }

            var b3dm = new B3dm
            {
                B3dmHeader = b3dmHeader,
                GlbData = glbBuffer,
                FeatureTableJson = featureTableJson,
                FeatureTableBinary = featureTableBytes,
                BatchTableJson = batchTableJson,
                BatchTableBinary = batchTableBytes
            };
            return b3dm;
        }

        public static B3dm ReadB3dm(Stream stream)
        {
            using (var reader = new BinaryReader(stream))
            {
                var b3dm = ReadB3dm(reader);
                return b3dm;
            }
        }
    }
}
