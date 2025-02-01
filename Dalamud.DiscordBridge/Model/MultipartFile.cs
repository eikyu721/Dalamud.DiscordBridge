using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Dalamud.DiscordBridgeFork.Model
{
    public struct MultipartFile
    {
        public Stream Stream { get; }
        public string Filename { get; }
        public string ContentType { get; }

        public MultipartFile(Stream stream, string filename, string contentType = null)
        {
            Stream = stream;
            Filename = filename;
            ContentType = contentType;
        }
    }
}
